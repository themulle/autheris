namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Architecture Phase 2 (WP 2.2): Unified Policy Decision Point (PDP).
/// Evaluates ReBAC graph relations, Casbin ABAC environment conditions, and Consent Engine
/// permissions under a single coherent pipeline and caches the authoritative decision.
/// </summary>
public sealed class UnifiedPolicyDecisionPoint : IUnifiedPolicyDecisionPoint
{
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<UnifiedPolicyDecisionPoint> _logger;

    public UnifiedPolicyDecisionPoint(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IOptions<GatewayOptions> options,
        ILogger<UnifiedPolicyDecisionPoint> logger,
        IPolicyEnforcementService? policyEnforcementService = null,
        IRebacEvaluator? rebacEvaluator = null)
    {
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policyEnforcementService = policyEnforcementService;
        _rebacEvaluator = rebacEvaluator;
    }

    public async Task<TableAccessDecision> EvaluateAccessAsync(
        TableIdentifier table,
        TableMetadata metadata,
        SecurityPrincipalContext securityContext,
        IReadOnlyList<string>? requestedColumns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(securityContext);

        var tenantId = securityContext.TenantId;
        var userSid = securityContext.UserSid;
        var groupSids = securityContext.GroupSids;
        var roles = new HashSet<string>(securityContext.TenantRoles.Concat(securityContext.ClusterRoles), StringComparer.OrdinalIgnoreCase);

        // 1. ReBAC Evaluation Gate
        if (_rebacEvaluator != null && _rebacEvaluator.IsEnabled)
        {
            var rebacRequest = new RebacCheckRequest(
                tenantId.Value,
                userSid.Value,
                "can_query",
                $"table:{table.Domain}.{table.TableName}");

            var rebacResult = await _rebacEvaluator.CheckAsync(rebacRequest, ct).ConfigureAwait(false);
            if (!rebacResult.Allowed)
            {
                _logger.LogWarning(
                    "ReBAC authorization denied for subject {Subject} on table {Table}. Reason: {Reason}",
                    userSid.Value, table, rebacResult.Reason);

                return TableAccessDecision.Denied(table, "ReBAC Access Denied: Not authorized by relationship graph.");
            }
        }

        // 2. Consent Engine Resolution
        TableAccessDecision decision;
        if (_options.Value.IsConsentBypassed)
        {
            decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }
        else
        {
            var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
            var cached = await _cacheService.GetCachedDecisionAsync(tenantId, userSid, table, contextHash, ct).ConfigureAwait(false);

            if (cached == null)
            {
                var allSubjects = groupSids.Append(userSid).ToList();
                // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
                var epochAtLoad = await _cacheService.GetEpochSnapshotAsync(table, ct).ConfigureAwait(false);
                var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

                // Multi-tenancy isolation filter
                activeConsents = activeConsents.Where(c => c.TenantId == tenantId).ToList();

                decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

                var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(metadata.Table.IsHighlySensitive, activeConsents, DateTimeOffset.UtcNow);
                await _cacheService.SetCachedDecisionAsync(tenantId, userSid, table, decision, ttl, contextHash, epochAtLoad, ct).ConfigureAwait(false);
            }
            else
            {
                decision = cached;
            }
        }

        if (!decision.IsAllowed)
        {
            return decision;
        }

        // 3. Casbin ABAC & Row-Level Security Evaluation Gate
        if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenantId) && !_options.Value.IsConsentBypassed)
        {
            var secEvaluationContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids.ToList(),
                Tenant: tenantId,
                TargetTable: table,
                RequestedColumns: requestedColumns ?? metadata.Columns.Select(c => c.ColumnName).ToList(),
                // RV-01: unknown client IP must never satisfy loopback/internal-network allow rules (fail-closed).
                ClientIp: securityContext.ClientIp ?? IPAddress.None,
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: null,
                Attributes: null
            );

            var casbinDecision = await _policyEnforcementService.EvaluatePolicyAsync(secEvaluationContext, ct).ConfigureAwait(false);
            if (!casbinDecision.IsAllowed)
            {
                return TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{userSid.Value}' in tenant '{tenantId.Value}'.");
            }

            if (!string.IsNullOrWhiteSpace(casbinDecision.CombinedRowFilterSql))
            {
                var mergedFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
                    ? $"({decision.CombinedRowFilterSql}) AND ({casbinDecision.CombinedRowFilterSql})"
                    : casbinDecision.CombinedRowFilterSql;

                decision = decision with { CombinedRowFilterSql = mergedFilter };
            }
        }

        return decision;
    }
}
