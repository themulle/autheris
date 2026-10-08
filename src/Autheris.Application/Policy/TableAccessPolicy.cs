namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>Where the ReBAC <c>can_query</c> gate applies (see <see cref="RebacTableGate"/>).</summary>
public enum RebacEnforcement
{
    /// <summary>OData, GraphQL, WebSQL, procedures: only with <c>Rebac.EnforceOnQueryPaths</c>.</summary>
    QueryPaths,

    /// <summary>Unified PDP (MCP-RAG) and DuckDB OLAP: whenever ReBAC is enabled.</summary>
    WhenEnabled
}

/// <summary>
/// One caller and one catalog table. <paramref name="Claims"/> feed the Casbin attributes and the purpose;
/// <paramref name="ClientIp"/> overrides the request IP resolver (unknown IPs are <see cref="IPAddress.None"/>).
/// </summary>
public sealed record TableAccessQuery(
    Sid UserSid,
    TenantId Tenant,
    IReadOnlySet<Sid> GroupSids,
    IReadOnlySet<string> Roles,
    TableMetadata Metadata,
    IEnumerable<Claim>? Claims = null,
    IReadOnlyList<string>? RequestedColumns = null,
    RebacEnforcement Rebac = RebacEnforcement.QueryPaths,
    IPAddress? ClientIp = null,
    IReadOnlyDictionary<string, object?>? ExtraAttributes = null,
    FilterObjectKinds ObjectKind = FilterObjectKinds.Relation,
    IReadOnlySet<Sid>? AllUserSids = null)
{
    public static TableAccessQuery ForPrincipal(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenant,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        RebacEnforcement rebac = RebacEnforcement.QueryPaths,
        IReadOnlyDictionary<string, object?>? extraAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new TableAccessQuery(
            userSid,
            tenant,
            principal.GetGroupSids(),
            principal.GetUserRoles(),
            metadata,
            principal.Claims,
            requestedColumns,
            rebac,
            ClientIp: null,
            extraAttributes,
            AllUserSids: principal.GetAllUserSids());
    }
}

/// <summary>
/// Architecture 1 (SQL2-6, POL-6, API-10): the single access decision for a caller on a catalog table. REST/OData/GraphQL
/// (<see cref="GatewayExecutionService"/>), WebSQL, stored procedures, DuckDB OLAP and the unified PDP all decide here, in
/// the same order:
/// <list type="number">
/// <item>ReBAC <c>can_query</c> (<see cref="RebacEnforcement"/>);</item>
/// <item>consents: bypass switch, else decision cache (epoch compare-and-set) or the consent repository, tenant-filtered;</item>
/// <item>Casbin ABAC only when consent allowed and the switch is off; a deny wins, its restriction is merged by
/// <see cref="Restrict"/>.</item>
/// </list>
/// Callers keep what is specific to them (catalog lookup, tenant selection, audit, SQL validation of the merged filter).
/// </summary>
public sealed class TableAccessPolicy
{
    public const string RebacDeniedReason = "ReBAC Access Denied: Not authorized by relationship graph.";

    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService? _cacheService;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly GatewayOptions? _options;
    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver _mandatoryFilters;

    /// <param name="mandatoryFilters">
    /// Virtual filters; required on purpose: a decision point without it would silently skip them. Use
    /// <see cref="Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver"/> only where there are none by design.
    /// </param>
    public TableAccessPolicy(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService? cacheService,
        IPolicyEnforcementService? policyEnforcementService,
        IRebacEvaluator? rebacEvaluator,
        IClientIpResolver? clientIpResolver,
        GatewayOptions? options,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver mandatoryFilters)
    {
        _mandatoryFilters = mandatoryFilters ?? throw new ArgumentNullException(nameof(mandatoryFilters));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService;
        _policyEnforcementService = policyEnforcementService;
        _rebacEvaluator = rebacEvaluator;
        _clientIpResolver = clientIpResolver;
        _options = options;
    }

    public async Task<TableAccessDecision> DecideAsync(TableAccessQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var table = query.Metadata.Identifier;

        if (await IsRebacDeniedAsync(query, ct).ConfigureAwait(false))
        {
            return TableAccessDecision.Denied(table, RebacDeniedReason);
        }

        var consentBypassed = _options?.IsConsentBypassed == true;
        var decision = consentBypassed
            ? TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
            : await ResolveConsentAsync(query, ct).ConfigureAwait(false);

        // Virtual filters: restrictive, AND with the consent decision (after the consent cache, which stays free of them;
        // also with the consent bypass, decision 1). They never turn a denial into an allow.
        if (decision.IsAllowed)
        {
            var mandatory = await _mandatoryFilters.ResolveAsync(
                new Autheris.Application.VirtualFilters.MandatoryFilterQuery(query.UserSid, query.GroupSids, query.Roles, query.Tenant, query.Metadata, query.ObjectKind, query.AllUserSids),
                ct).ConfigureAwait(false);
            if (mandatory.IsDenied)
            {
                return TableAccessDecision.Denied(table, mandatory.DenyReason ?? "Denied by virtual filters.");
            }

            if (mandatory.PredicateSql != null)
            {
                decision = decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters);
            }
        }

        if (!decision.IsAllowed || consentBypassed ||
            _policyEnforcementService == null || !_policyEnforcementService.HasPolicies(query.Tenant))
        {
            return decision;
        }

        var policyDecision = await _policyEnforcementService.EvaluatePolicyAsync(BuildEvaluationContext(query), ct).ConfigureAwait(false);
        return policyDecision.IsAllowed
            ? Restrict(decision, policyDecision, query.Metadata)
            : TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{query.UserSid.Value}' in tenant '{query.Tenant.Value}'.");
    }

    /// <summary>
    /// Evaluates whether the given principal has permission to perform write / DML operations on the specified table.
    /// Requires an explicit Casbin action "write" (or "*") and consent clearance where applicable.
    /// </summary>
    public async Task<bool> CanWriteTableAsync(
        ClaimsPrincipal user,
        TenantId tenant,
        TableMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(metadata);

        var userSid = user.GetUserSid() ?? new Sid(user.Identity?.Name ?? "anonymous");
        var query = TableAccessQuery.ForPrincipal(
            user,
            userSid,
            tenant,
            metadata,
            extraAttributes: new Dictionary<string, object?>
            {
                ["gql.action"] = "write",
                ["action"] = "write"
            });

        var decision = await DecideAsync(query, ct).ConfigureAwait(false);
        return decision.IsAllowed;
    }

    /// <summary>
    /// Evaluates whether the given principal has permission to perform write / DML operations on the specified table identifier.
    /// </summary>
    public Task<bool> CanWriteTableAsync(
        ClaimsPrincipal user,
        TenantId tenant,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var metadata = new TableMetadata { Identifier = table };
        return CanWriteTableAsync(user, tenant, metadata, ct);
    }

    /// <summary>
    /// SEC C-03: applies a Casbin decision as an additional restriction on the consent decision. Column levels can only be
    /// lowered (an explicit Clear can only come from consent), row filters are combined with AND, and row filter parameters
    /// are merged; two filters binding the same name to different values deny (ambiguous, fail-closed).
    /// </summary>
    public static TableAccessDecision Restrict(TableAccessDecision consentDecision, TableAccessDecision policyDecision, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(consentDecision);
        ArgumentNullException.ThrowIfNull(policyDecision);
        ArgumentNullException.ThrowIfNull(metadata);

        var mergedColumns = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            var policyLevel = policyDecision.GetColumnAccess(col.ColumnName);
            if (consentDecision.ColumnAccess.TryGetValue(col.ColumnName, out var explicitLevel))
            {
                mergedColumns[col.ColumnName] = explicitLevel < policyLevel ? explicitLevel : policyLevel;
            }
            else if (consentDecision.HasUnconstrainedColumnAllow && policyLevel != ColumnAccessLevel.Clear)
            {
                mergedColumns[col.ColumnName] = policyLevel;
            }
        }

        var mergedFilter = consentDecision.CombinedRowFilterSql;
        if (!string.IsNullOrWhiteSpace(policyDecision.CombinedRowFilterSql))
        {
            mergedFilter = !string.IsNullOrWhiteSpace(mergedFilter)
                ? $"({mergedFilter}) AND ({policyDecision.CombinedRowFilterSql})"
                : policyDecision.CombinedRowFilterSql;
        }

        var mergedParameters = consentDecision.RowFilterParameters;
        if (policyDecision.RowFilterParameters is { Count: > 0 })
        {
            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { consentDecision.RowFilterParameters, policyDecision.RowFilterParameters })
            {
                foreach (var (name, value) in source ?? new Dictionary<string, object?>())
                {
                    if (merged.TryGetValue(name, out var existing) && !Equals(existing, value))
                    {
                        return TableAccessDecision.Denied(consentDecision.Table, "Conflicting row-level security parameters.");
                    }

                    merged[name] = value;
                }
            }

            mergedParameters = merged;
        }

        return consentDecision with
        {
            ColumnAccess = mergedColumns,
            CombinedRowFilterSql = mergedFilter,
            RowFilterParameters = mergedParameters
        };
    }

    private async Task<bool> IsRebacDeniedAsync(TableAccessQuery query, CancellationToken ct)
    {
        if (_options?.IsRebacBypassed == true)
        {
            return false;
        }

        var enforced = query.Rebac switch
        {
            RebacEnforcement.WhenEnabled => _rebacEvaluator is { IsEnabled: true },
            _ => RebacTableGate.IsEnforcedOnQueryPaths(_options)
        };

        return enforced &&
               !await RebacTableGate.IsAllowedAsync(_rebacEvaluator, query.Tenant, query.UserSid, query.Metadata.Identifier, ct).ConfigureAwait(false);
    }

    private async Task<TableAccessDecision> ResolveConsentAsync(TableAccessQuery query, CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        string? contextHash = null;
        if (_cacheService != null)
        {
            contextHash = IConsentCacheService.ComputeSubjectContextHash(query.GroupSids, query.Roles);
            var cached = await _cacheService.GetCachedDecisionAsync(query.Tenant, query.UserSid, table, contextHash, ct).ConfigureAwait(false);
            if (cached != null)
            {
                return cached;
            }
        }

        // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
        var epochAtLoad = _cacheService != null ? await _cacheService.GetEpochSnapshotAsync(table, ct).ConfigureAwait(false) : 0;
        IEnumerable<Sid> userSids = query.AllUserSids != null && query.AllUserSids.Count > 0
            ? query.AllUserSids
            : [query.UserSid];
        var subjects = userSids
            .Concat(query.GroupSids)
            .Distinct()
            .ToList();
        var activeConsents = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, DateTimeOffset.UtcNow, query.Tenant, ct).ConfigureAwait(false))
            .Where(c => c.TenantId == query.Tenant) // multi-tenancy isolation
            .ToList();

        var decision = _resolutionService.ResolveAccess(query.UserSid, query.GroupSids, query.Roles, table, activeConsents, query.Metadata.Dialect, query.AllUserSids);

        if (_cacheService != null)
        {
            // SEC: TTL bounded by the earliest consent ValidTo; shorter for highly sensitive tables.
            var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(query.Metadata.Table.IsHighlySensitive, activeConsents, DateTimeOffset.UtcNow);
            await _cacheService.SetCachedDecisionAsync(query.Tenant, query.UserSid, table, decision, ttl, contextHash!, epochAtLoad, ct).ConfigureAwait(false);
        }

        return decision;
    }

    private SecurityEvaluationContext BuildEvaluationContext(TableAccessQuery query)
    {
        var claims = query.Claims?.ToList() ?? [];
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims)
        {
            // SR15-08: Action claims from client tokens MUST NOT spoof gateway-internal action resolution!
            if (string.Equals(claim.Type, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "gql.action", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            attributes[claim.Type] = claim.Value;
        }

        var purpose = claims.FirstOrDefault(c => c.Type == "purpose")?.Value ?? claims.FirstOrDefault(c => c.Type == "purpose_id")?.Value;

        foreach (var (key, value) in query.ExtraAttributes ?? new Dictionary<string, object?>())
        {
            attributes[key] = value;
        }

        return new SecurityEvaluationContext(
            UserSid: query.UserSid,
            GroupSids: query.GroupSids,
            Tenant: query.Tenant,
            TargetTable: query.Metadata.Identifier,
            RequestedColumns: query.RequestedColumns ?? query.Metadata.Columns.Select(c => c.ColumnName).ToList(),
            // SEC H-4 / RV-01: an unknown client IP never satisfies network rules (fail-closed); never from token claims.
            ClientIp: query.ClientIp ?? _clientIpResolver?.ResolveClientIp() ?? IPAddress.None,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: purpose,
            Attributes: attributes,
            TargetDialect: query.Metadata.Dialect);
    }
}
