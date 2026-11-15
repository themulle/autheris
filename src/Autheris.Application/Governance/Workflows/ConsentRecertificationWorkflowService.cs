namespace Autheris.Application.Governance.Workflows;

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Proactive Recertification workflow engine.
/// Scans for expiring temporary consents within the warning window (default 3 days)
/// and records structured recertification audit events.
/// </summary>
public sealed class ConsentRecertificationWorkflowService : IConsentRecertificationService
{
    public const int DefaultRecertificationWarningDays = 3;

    private readonly IConsentRepository _consentRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ILogger<ConsentRecertificationWorkflowService> _logger;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentlyTriggered = new();

    public ConsentRecertificationWorkflowService(
        IConsentRepository consentRepo,
        ILogger<ConsentRecertificationWorkflowService> logger,
        IAuditLogRepository auditRepo,
        IGrantPolicy? grantPolicy = null,
        ITableMetadataRepository? metadataRepo = null)
    {
        _ = grantPolicy;
        _ = metadataRepo;
        _consentRepo = consentRepo ?? throw new ArgumentNullException(nameof(consentRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
    }

    public async Task<int> ScanAndTriggerExpiringConsentRecertificationsAsync(
        TimeSpan? warningWindow = null,
        CancellationToken ct = default)
    {
        var window = warningWindow ?? TimeSpan.FromDays(DefaultRecertificationWarningDays);
        var threshold = DateTimeOffset.UtcNow.Add(window);

        _logger.LogInformation("Scanning for consents expiring before {Threshold}...", threshold);

        var expiringConsents = await _consentRepo.GetExpiringConsentsAsync(threshold, ct: ct).ConfigureAwait(false);
        int dispatchedCount = 0;

        // Cleanup stale entries older than 24h
        var staleCutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var kvp in _recentlyTriggered)
        {
            if (kvp.Value < staleCutoff) _recentlyTriggered.TryRemove(kvp.Key, out _);
        }

        foreach (var consent in expiringConsents)
        {
            if (_recentlyTriggered.TryGetValue(consent.Id, out var triggeredAt) && (DateTimeOffset.UtcNow - triggeredAt) < TimeSpan.FromHours(24))
            {
                _logger.LogDebug("Skipping recertification for consent '{ConsentId}' (already triggered within 24h).", consent.Id);
                continue;
            }

            var requesterSid = consent.GranteeSid ?? new Sid("UNKNOWN_USER");
            var remainingHours = (int)Math.Max(1, (consent.ValidTo - DateTimeOffset.UtcNow).TotalHours);

            var details = new
            {
                ConsentId = consent.Id,
                Tenant = consent.TenantId.Value,
                Table = consent.TableIdentifier.ToString(),
                CurrentValidTo = consent.ValidTo,
                ExpiresInHours = remainingHours,
                RequesterSid = requesterSid.Value
            };

            var auditEntry = new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = AuditEventTypes.ConsentRecertificationRequired,
                ActorSid = requesterSid,
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(details)
            };

            await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);
            _recentlyTriggered[consent.Id] = DateTimeOffset.UtcNow;
            dispatchedCount++;

            _logger.LogInformation(
                "Recorded recertification audit event for consent '{ConsentId}', table '{Table}', expiring in {Hours}h.",
                consent.Id, consent.TableIdentifier, remainingHours);
        }

        return dispatchedCount;
    }
}
