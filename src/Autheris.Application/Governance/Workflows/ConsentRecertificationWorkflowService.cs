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
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Proactive Recertification and Extension workflow engine.
/// Scans for expiring temporary consents within the warning window (default 3 days),
/// records structured recertification audit events, and handles approved consent extensions.
/// </summary>
public sealed class ConsentRecertificationWorkflowService : IConsentRecertificationService
{
    public const int DefaultRecertificationWarningDays = 3;

    private readonly IConsentRepository _consentRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ILogger<ConsentRecertificationWorkflowService> _logger;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentlyTriggered = new();
    private readonly IGrantPolicy _grantPolicy;
    private readonly ITableMetadataRepository? _metadataRepo;

    public ConsentRecertificationWorkflowService(
        IConsentRepository consentRepo,
        ILogger<ConsentRecertificationWorkflowService> logger,
        IAuditLogRepository auditRepo,
        IGrantPolicy? grantPolicy = null,
        ITableMetadataRepository? metadataRepo = null)
    {
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _metadataRepo = metadataRepo;
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

        var now = DateTimeOffset.UtcNow;

        // PERS-04: only an active consent is extended; a revoked or expired one needs a new consent (and its approval).
        if (consent.IsRevoked || consent.ValidTo <= now)
        {
            _logger.LogWarning("Rejected extension of non-extendable consent '{ConsentId}'.", consentId);
            throw new ConsentNotExtendableException(consentId);
        }

        var newValidTo = consent.ValidTo.Add(extensionDuration);

        // WP-SC-16 rule 3: an extension of an individual grant is a new approval by someone other than the grantee, capped by the
        // class maximum (now + max), with a role-conversion hint after repeated renewals and a campaign decision from the 4th.
        var sensitivity = GrantSensitivity.Sensitive; // fail closed when the table cannot be classified
        if (_metadataRepo != null)
        {
            var meta = await _metadataRepo.GetTableMetadataAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            if (meta != null)
            {
                sensitivity = GrantClassifier.Classify([meta]);
            }
        }

        var granteeRef = consent.GranteeSid?.Value ?? consent.RoleName ?? string.Empty;
        var renewal = new GrantRequest(
            consent.GranteeType, granteeRef, GrantKind.Renewal, newValidTo, now, sensitivity,
            Renewer: approverSid.Value, RenewalCount: consent.RenewalCount, Justification: justification);
        var decision = _grantPolicy.Evaluate(renewal);
        if (!decision.Allowed && decision.ErrorCode == GrantErrorCodes.ExceedsMaximum && decision.MaxValidTo is { } cap)
        {
            newValidTo = cap;
            decision = _grantPolicy.Evaluate(renewal with { ValidTo = newValidTo });
        }

        GrantPolicyViolationException.ThrowIfDenied(decision);
        if (newValidTo <= consent.ValidTo)
        {
            _logger.LogWarning("Rejected extension of consent '{ConsentId}': the capped expiry is not later than the current one.", consentId);
            throw new ConsentNotExtendableException(consentId);
        }

        await _consentRepo.ExtendConsentExpiryAsync(consentId, newValidTo, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Extended consent '{ConsentId}' until {NewValidTo} by approver '{Approver}'. Justification: {Justification}",
            consentId, newValidTo, approverSid.Value, justification);

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
                RenewalCount = consent.RenewalCount + 1,
                SuggestRoleConversion = decision.SuggestRoleConversion,
                Justification = justification
            })
        };
        await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);

        return true;
    }
}
