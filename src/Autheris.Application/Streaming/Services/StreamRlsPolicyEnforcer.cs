namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Enforces the gateway access model on CDC streaming events.
/// SEC H-09: Uses the same decision chain as <see cref="GatewayExecutionService"/>:
/// consent (via <see cref="IConsentResolutionService"/> and the consent cache) -> Casbin ABAC as additional gate and row filter
/// -> catalog masking (sensitive columns / ColumnMaskingRules are masked unless explicitly released as Clear).
/// </summary>
public sealed class StreamRlsPolicyEnforcer : IStreamRlsPolicyEnforcer
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly IPolicyEnforcementService _policyEnforcementService;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IEpochValidationService _epochValidationService;
    private readonly ILogger<StreamRlsPolicyEnforcer> _logger;
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly GatewayOptions? _options;
    private readonly Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? _rebacEvaluator;

    public StreamRlsPolicyEnforcer(
        IPolicyEnforcementService policyEnforcementService,
        ITableMetadataRepository metadataRepository,
        IColumnMaskingProvider maskingProvider,
        IEpochValidationService epochValidationService,
        ILogger<StreamRlsPolicyEnforcer> logger,
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IClientIpResolver? clientIpResolver = null,
        IOptions<GatewayOptions>? options = null,
        Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? rebacEvaluator = null)
    {
        _policyEnforcementService = policyEnforcementService ?? throw new ArgumentNullException(nameof(policyEnforcementService));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _epochValidationService = epochValidationService ?? throw new ArgumentNullException(nameof(epochValidationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _clientIpResolver = clientIpResolver;
        _options = options?.Value;
        _rebacEvaluator = rebacEvaluator;
    }

    public async ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(
        CdcEvent cdcEvent,
        ClaimsPrincipal subscriber,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        ArgumentNullException.ThrowIfNull(subscriber);

        // 1. Tenant Isolation Check
        var subscriberTenant = subscriber.FindFirst("tenant_id")?.Value
            ?? subscriber.FindFirst("tenant")?.Value
            ?? subscriber.FindFirst("tid")?.Value
            ?? TenantId.LegacySingleTenant.Value;

        if (string.IsNullOrWhiteSpace(cdcEvent.TenantId) ||
            !string.Equals(cdcEvent.TenantId, subscriberTenant, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Streaming event '{EventId}' dropped due to tenant mismatch (Event: {EventTenant}, Subscriber: {SubTenant})",
                cdcEvent.EventId, cdcEvent.TenantId, subscriberTenant);
            return StreamSecurityDecision.Denied("Tenant mismatch");
        }

        if (!TenantId.TryParse(subscriberTenant, out var tenantId))
        {
            return StreamSecurityDecision.Denied("Tenant mismatch");
        }

        // 2. Extract Raw Payload based on Operation
        var rawPayload = cdcEvent.Operation == CdcOperation.Delete
            ? (cdcEvent.Before ?? cdcEvent.After)
            : (cdcEvent.After ?? cdcEvent.Before);

        if (rawPayload == null || rawPayload.Count == 0)
        {
            return StreamSecurityDecision.Denied("Empty event payload");
        }

        // 3. User Identity Extraction (no anonymous fallback: a subscriber without a SID gets nothing)
        var userSidNullable = subscriber.GetUserSid();
        if (userSidNullable == null)
        {
            return StreamSecurityDecision.Denied("Access denied: no subject identifier");
        }

        var userSid = userSidNullable.Value;
        var groupSids = subscriber.GetGroupSids();
        var roles = subscriber.GetUserRoles();

        // 4. Catalog metadata is mandatory (dialect, column list, masking rules) -> fail-closed
        TableMetadata? metadata;
        TableAccessDecision decision;
        try
        {
            metadata = await _metadataRepository.GetTableMetadataAsync(cdcEvent.Table, ct).ConfigureAwait(false);
            if (metadata == null)
            {
                _logger.LogDebug("Streaming event '{EventId}' dropped: table '{Table}' not found in catalog", cdcEvent.EventId, cdcEvent.Table.ToQualifiedName());
                return StreamSecurityDecision.Denied("Table not found in catalog");
            }

            // 5. Consent decision (same chain as GatewayExecutionService)
            decision = await ResolveConsentDecisionAsync(tenantId, userSid, groupSids, roles, cdcEvent.Table, metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate streaming consent decision for event '{EventId}' - failing closed", cdcEvent.EventId);
            return StreamSecurityDecision.Denied("Policy evaluation error");
        }

        if (metadata == null)
        {
            return StreamSecurityDecision.Denied("Table not found in catalog");
        }

        if (!decision.IsAllowed)
        {
            _logger.LogDebug(
                "Streaming event '{EventId}' on table '{Table}' denied for subscriber '{UserSid}' by consent policy",
                cdcEvent.EventId, cdcEvent.Table.ToQualifiedName(), userSid.Value);
            return StreamSecurityDecision.Denied("Access denied by consent policy");
        }

        // 6. Casbin ABAC Policy Enforcement as additional gate + row filter (with full ABAC context)
        if (_policyEnforcementService.HasPolicies(tenantId) && _options?.IsConsentBypassed != true)
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in subscriber.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            var purpose = subscriber.FindFirst("purpose")?.Value ?? subscriber.FindFirst("purpose_id")?.Value;

            var evalContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenantId,
                TargetTable: cdcEvent.Table,
                RequestedColumns: rawPayload.Keys.ToList(),
                ClientIp: ResolveClientIp(subscriber),
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: purpose,
                Attributes: attributes
            );

            TableAccessDecision casbinDecision;
            try
            {
                casbinDecision = await _policyEnforcementService.EvaluatePolicyAsync(evalContext, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate streaming ABAC policy for event '{EventId}' - failing closed", cdcEvent.EventId);
                return StreamSecurityDecision.Denied("Policy evaluation error");
            }

            if (!casbinDecision.IsAllowed)
            {
                _logger.LogDebug(
                    "Streaming event '{EventId}' on table '{Table}' denied for subscriber '{UserSid}' by ABAC policy",
                    cdcEvent.EventId, cdcEvent.Table.ToQualifiedName(), userSid.Value);
                return StreamSecurityDecision.Denied("Access denied by ABAC policy");
            }

            if (!string.IsNullOrWhiteSpace(casbinDecision.CombinedRowFilterSql))
            {
                var mergedFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
                    ? $"({decision.CombinedRowFilterSql}) AND ({casbinDecision.CombinedRowFilterSql})"
                    : casbinDecision.CombinedRowFilterSql;

                decision = decision with { CombinedRowFilterSql = mergedFilter };
            }
        }

        // 6b. ReBAC Policy Enforcement (Google Zanzibar) on Streaming Events
        if (_rebacEvaluator != null && _rebacEvaluator.IsEnabled && _options?.Rebac?.EnforceOnStreaming == true)
        {
            var targetTable = $"table:{cdcEvent.Table.ToQualifiedName()}";
            var rebacReq = new Autheris.Domain.Model.RebacCheckRequest(tenantId.Value, userSid.Value, "subscriber", targetTable);
            try
            {
                var rebacDecision = await _rebacEvaluator.CheckAsync(rebacReq, ct).ConfigureAwait(false);
                if (!rebacDecision.Allowed)
                {
                    _logger.LogDebug(
                        "Streaming event '{EventId}' on table '{Table}' denied for subscriber '{UserSid}' by ReBAC policy",
                        cdcEvent.EventId, cdcEvent.Table.ToQualifiedName(), userSid.Value);
                    return StreamSecurityDecision.Denied("Access denied by ReBAC policy");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate streaming ReBAC policy for event '{EventId}' - failing closed", cdcEvent.EventId);
                return StreamSecurityDecision.Denied("Policy evaluation error");
            }
        }

        // 7. In-Stream Row-Level Security Filtering (Fail-Closed, SQL three-valued logic)
        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            if (!MatchesStreamingRowFilter(rawPayload, decision.CombinedRowFilterSql))
            {
                _logger.LogDebug(
                    "Streaming event '{EventId}' filtered out by in-stream row filter for subscriber '{UserSid}'",
                    cdcEvent.EventId, userSid.Value);
                return StreamSecurityDecision.Denied("Filtered by row-level security");
            }
        }

        // 8. In-Stream Column Masking and Redaction (Consent level + catalog sensitivity)
        var maskedResult = new Dictionary<string, object?>(rawPayload.Count, StringComparer.OrdinalIgnoreCase);
        bool maskingDisabled = _options?.IsColumnMaskingDisabled == true;

        foreach (var (columnName, rawValue) in rawPayload)
        {
            var catalogColumn = metadata.GetColumn(columnName);
            if (catalogColumn == null)
            {
                // Zero-Trust: columns unknown to the catalog are never streamed (mirrors the query path, which only projects catalog columns)
                continue;
            }

            var accessLevel = decision.GetEffectiveColumnAccess(catalogColumn.ColumnName, metadata);

            switch (accessLevel)
            {
                case ColumnAccessLevel.Deny:
                    // Completely strip column from streaming payload (zero leakage)
                    continue;

                case ColumnAccessLevel.Mask:
                    if (rawValue != null && !maskingDisabled)
                    {
                        var rule = metadata.ColumnMaskingRules.TryGetValue(catalogColumn.ColumnName, out var r)
                            ? r
                            : DefaultRedactRule;

                        // SEC D-3: HMAC pseudonyms are tenant-scoped.
                        rule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(
                            rule, tenantId.Value, _options?.DataMasking?.HmacKeyId);
                        maskedResult[columnName] = _maskingProvider.MaskValue(columnName, rawValue, rule);
                    }
                    else
                    {
                        maskedResult[columnName] = rawValue;
                    }
                    break;

                case ColumnAccessLevel.Clear:
                default:
                    maskedResult[columnName] = rawValue;
                    break;
            }
        }

        return StreamSecurityDecision.Allowed(maskedResult);
    }

    private async Task<TableAccessDecision> ResolveConsentDecisionAsync(
        TenantId tenantId,
        Sid userSid,
        HashSet<Sid> groupSids,
        HashSet<string> roles,
        TableIdentifier table,
        TableMetadata metadata,
        CancellationToken ct)
    {
        if (_options?.IsConsentBypassed == true)
        {
            return TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }

        var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
        var cached = await _cacheService.GetCachedDecisionAsync(tenantId, userSid, table, contextHash, ct).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        var now = DateTimeOffset.UtcNow;
        var allSubjects = groupSids.Append(userSid).ToList();
        // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
        var epochAtLoad = await _cacheService.GetEpochSnapshotAsync(table, ct).ConfigureAwait(false);
        var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, now, tenantId, ct).ConfigureAwait(false);

        // Multi-Tenancy Isolation: Filter active consents strictly for current tenant
        var tenantConsents = activeConsents
            .Where(c => c.TenantId == tenantId)
            .ToList();

        var decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, tenantConsents, metadata.Dialect);

        var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(metadata.Table.IsHighlySensitive, tenantConsents, now);
        await _cacheService.SetCachedDecisionAsync(tenantId, userSid, table, decision, ttl, contextHash, epochAtLoad, ct).ConfigureAwait(false);

        return decision;
    }

    private IPAddress ResolveClientIp(ClaimsPrincipal subscriber)
    {
        IPAddress? resolved = null;
        try
        {
            resolved = _clientIpResolver?.ResolveClientIp();
        }
        catch (InvalidOperationException)
        {
            // No HTTP context available for this (long-lived) subscription
            resolved = null;
        }

        // The HTTP resolver reports Loopback when no HttpContext is available (typical for long-lived subscriptions);
        // that value is not trustworthy as a client address and is therefore treated as unknown.
        if (resolved != null && !IPAddress.IsLoopback(resolved))
        {
            return resolved;
        }

        // SEC (Low): the token "ip" claim is caller-influenced and is not used as a fallback.
        // SEC H-09: Never fall back to Loopback (which may satisfy "internal network" ABAC rules).
        // SecurityEvaluationContext.ClientIp is non-nullable, so the unroutable IPAddress.None is used as "unknown".
        return IPAddress.None;
    }

    private static bool MatchesStreamingRowFilter(IReadOnlyDictionary<string, object?> payload, string filterSql)
    {
        return StreamingRowFilterAstEvaluator.Matches(payload, filterSql);
    }
}
