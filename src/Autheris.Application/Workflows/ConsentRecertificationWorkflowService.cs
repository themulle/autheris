namespace Autheris.Application.Workflows;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Proactive Recertification and Extension workflow engine.
/// Scans for expiring temporary consents within warning window (default 3 days),
/// dispatches recertification tasks via the ITSM outbox to ServiceNow / Jira,
/// and handles approved consent extensions.
/// </summary>
public sealed class ConsentRecertificationWorkflowService : IConsentRecertificationService
{
    private readonly IConsentRepository _consentRepo;
    private readonly IItsmOutboxRepository _outboxRepo;
    private readonly IAuditLogRepository? _auditRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ConsentRecertificationWorkflowService> _logger;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentlyTriggered = new();

    public ConsentRecertificationWorkflowService(
        IConsentRepository consentRepo,
        IItsmOutboxRepository outboxRepo,
        IOptions<GatewayOptions> options,
        ILogger<ConsentRecertificationWorkflowService> logger,
        IAuditLogRepository? auditRepo = null)
    {
        _consentRepo = consentRepo ?? throw new ArgumentNullException(nameof(consentRepo));
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditRepo = auditRepo;
    }

    public async Task<int> ScanAndTriggerExpiringConsentRecertificationsAsync(
        TimeSpan? warningWindow = null,
        CancellationToken ct = default)
    {
        var warningDays = _options.Value.Itsm.RecertificationWarningDays > 0
            ? _options.Value.Itsm.RecertificationWarningDays
            : 3;
        var window = warningWindow ?? TimeSpan.FromDays(warningDays);
        var threshold = DateTimeOffset.UtcNow.Add(window);

        _logger.LogInformation("Scanning for consents expiring before {Threshold}...", threshold);

        var expiringConsents = await _consentRepo.GetExpiringConsentsAsync(threshold, ct).ConfigureAwait(false);
        int dispatchedCount = 0;

        // Cleanup stale entries older than 24h
        var staleCutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var kvp in _recentlyTriggered)
        {
            if (kvp.Value < staleCutoff) _recentlyTriggered.TryRemove(kvp.Key, out _);
        }

        // Check pending messages to avoid cross-restart duplicates
        var pendingMessages = await _outboxRepo.GetPendingMessagesAsync(100, ct).ConfigureAwait(false);
        var pendingRequestIds = pendingMessages.Select(m => m.RequestId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var consent in expiringConsents)
        {
            if (_recentlyTriggered.TryGetValue(consent.Id, out var triggeredAt) && (DateTimeOffset.UtcNow - triggeredAt) < TimeSpan.FromHours(24))
            {
                _logger.LogDebug("Skipping recertification for consent '{ConsentId}' (already triggered within 24h).", consent.Id);
                continue;
            }

            var consentIdStr = consent.Id.ToString();
            if (pendingRequestIds.Contains(consentIdStr))
            {
                _logger.LogDebug("Skipping recertification for consent '{ConsentId}' (pending outbox message already exists).", consent.Id);
                continue;
            }

            var requesterSid = consent.GranteeSid ?? new Sid("UNKNOWN_USER");
            var remainingHours = (int)Math.Max(1, (consent.ValidTo - DateTimeOffset.UtcNow).TotalHours);

            var ticketPayload = new
            {
                ConsentId = consent.Id,
                Tenant = consent.TenantId.Value,
                RequesterSid = requesterSid.Value,
                TargetTable = consent.TableIdentifier.ToString(),
                CurrentValidTo = consent.ValidTo,
                ExpiresInHours = remainingHours,
                SuggestedExtensionDays = 30,
                Justification = $"Automated Recertification: Temporary access to table '{consent.TableIdentifier}' expires in {remainingHours} hours. Data Owner review required."
            };

            var outboxMessage = new ItsmOutboxMessage(
                Id: Guid.NewGuid().ToString("N"),
                RequestId: consent.Id.ToString(),
                TenantId: consent.TenantId.Value,
                EventType: "ConsentRecertificationRequired",
                PayloadJson: JsonSerializer.Serialize(ticketPayload),
                PreferredSystem: _options.Value.Itsm.DefaultSystem.ToString(),
                Status: ItsmOutboxStatus.Pending,
                RetryCount: 0,
                MaxRetries: 5,
                CreatedAt: DateTimeOffset.UtcNow
            );

            await _outboxRepo.EnqueueAsync(outboxMessage, ct).ConfigureAwait(false);
            _recentlyTriggered[consent.Id] = DateTimeOffset.UtcNow;
            dispatchedCount++;

            _logger.LogInformation(
                "Enqueued recertification outbox ticket for consent '{ConsentId}', table '{Table}', expiring in {Hours}h.",
                consent.Id, consent.TableIdentifier, remainingHours);
        }

        return dispatchedCount;
    }

    public async Task<bool> ExtendConsentExpiryAsync(
        Guid consentId,
        TimeSpan extensionDuration,
        Sid approverSid,
        string justification,
        CancellationToken ct = default)
    {
        if (consentId == Guid.Empty)
        {
            throw new ArgumentException("ConsentId must not be empty.", nameof(consentId));
        }

        if (extensionDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extensionDuration), "Extension duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(approverSid.Value))
        {
            throw new ArgumentException("Approver SID must be specified.", nameof(approverSid));
        }

        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new ArgumentException("Justification must be provided.", nameof(justification));
        }

        var consent = await _consentRepo.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        if (consent == null)
        {
            _logger.LogWarning("Cannot extend non-existent consent '{ConsentId}'.", consentId);
            return false;
        }

        if (consent.IsRevoked)
        {
            throw new InvalidOperationException($"Cannot extend revoked consent '{consentId}'.");
        }

        var newValidTo = (consent.ValidTo > DateTimeOffset.UtcNow ? consent.ValidTo : DateTimeOffset.UtcNow).Add(extensionDuration);
        await _consentRepo.ExtendConsentExpiryAsync(consentId, newValidTo, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Extended consent '{ConsentId}' until {NewValidTo} by approver '{Approver}'. Justification: {Justification}",
            consentId, newValidTo, approverSid.Value, justification);

        if (_auditRepo != null)
        {
            var auditEntry = new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = "CONSENT_RECERTIFIED_AND_EXTENDED",
                ActorSid = approverSid,
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consentId,
                    OldValidTo = consent.ValidTo,
                    NewValidTo = newValidTo,
                    ExtensionDays = extensionDuration.TotalDays,
                    Justification = justification
                })
            };
            await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);
        }

        return true;
    }
}
