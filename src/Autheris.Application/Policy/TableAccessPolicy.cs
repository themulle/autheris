namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;

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
    IReadOnlySet<Sid>? AllUserSids = null,
    SchemaContractDefinition? Contract = null)
{
    public static TableAccessQuery ForPrincipal(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenant,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        RebacEnforcement rebac = RebacEnforcement.QueryPaths,
        IReadOnlyDictionary<string, object?>? extraAttributes = null,
        SchemaContractDefinition? contract = null)
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
            AllUserSids: principal.GetAllUserSids(),
            Contract: contract);
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
    private readonly ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IReadOnlyList<AccessProfile> Profiles, DateTimeOffset ExpireAt)> _profileCache = new();
    private static Microsoft.Extensions.Caching.Memory.IMemoryCache? s_activeMemoryCache;

    public static void InvalidateCache(TenantId tenant, string subject, Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null)
    {
        var cacheKey = $"access_profile:{tenant.Value}:{subject}";
        _profileCache.TryRemove(cacheKey, out _);
        (memoryCache ?? s_activeMemoryCache)?.Remove(cacheKey);
    }

    public static void ClearCache()
    {
        _profileCache.Clear();
    }

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
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver mandatoryFilters,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null)
    {
        _mandatoryFilters = mandatoryFilters ?? throw new ArgumentNullException(nameof(mandatoryFilters));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService;
        _policyEnforcementService = policyEnforcementService;
        _rebacEvaluator = rebacEvaluator;
        _clientIpResolver = clientIpResolver;
        _options = options;
        _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        if (memoryCache != null)
        {
            s_activeMemoryCache = memoryCache;
        }
    }

    public async Task<TableAccessDecision> DecideAsync(TableAccessQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var table = query.Metadata.Identifier;

        // SG-12: Inactive tables are treated as not found / denied centrally
        if (!query.Metadata.Table.IsActive)
        {
            return TableAccessDecision.Denied(table, $"Table '{table.ToQualifiedName()}' is not active.");
        }

        // SR15-51 / SG-16: Enforce schema contracts at query execution time
        var contract = query.Contract;
        if (contract == null && _contractManager != null && _contractManager.IsEnabled)
        {
            var claimContract = query.Claims?.FirstOrDefault(c => string.Equals(c.Type, "contract", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(claimContract))
            {
                if (!_contractManager.HasContract(claimContract))
                {
                    return TableAccessDecision.Denied(table,
                        $"Schema Contract Denial: Unknown schema contract '{claimContract}' specified in caller token.");
                }
                contract = _contractManager.GetContract(claimContract);
            }
            else
            {
                var defaultContract = _contractManager.DefaultContract;
                if (!string.IsNullOrWhiteSpace(defaultContract) && _contractManager.HasContract(defaultContract))
                {
                    contract = _contractManager.GetContract(defaultContract);
                }
            }
        }

        if (contract != null)
        {
            if (contract.AllowedTables.Count > 0 &&
                !contract.AllowedTables.Contains(table.ToString()) &&
                !contract.AllowedTables.Contains(table.TableName))
            {
                return TableAccessDecision.Denied(table,
                    $"Schema Contract Denial: Table '{table.ToQualifiedName()}' is not permitted under contract '{contract.Name}'.");
            }

            var allTableTags = (query.Metadata.Table.Tags ?? Array.Empty<string>())
                .Concat(string.IsNullOrWhiteSpace(query.Metadata.Table.Sensitivity) ? Array.Empty<string>() : [query.Metadata.Table.Sensitivity])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (contract.ExcludedTags.Count > 0)
            {
                var matchedExcluded = allTableTags.FirstOrDefault(t => contract.ExcludedTags.Contains(t));
                if (matchedExcluded != null)
                {
                    return TableAccessDecision.Denied(table,
                        $"Schema Contract Denial: Table '{table.ToQualifiedName()}' with tag '{matchedExcluded}' is excluded under contract '{contract.Name}'.");
                }
            }

            if (contract.IncludedTags.Count > 0 &&
                (allTableTags.Count == 0 || !allTableTags.Any(t => contract.IncludedTags.Contains(t))))
            {
                return TableAccessDecision.Denied(table,
                    $"Schema Contract Denial: Table '{table.ToQualifiedName()}' is not in included tags under contract '{contract.Name}'.");
            }
        }

        if (await IsRebacDeniedAsync(query, ct).ConfigureAwait(false))
        {
            return TableAccessDecision.Denied(table, RebacDeniedReason);
        }

        var consentBypassed = _options?.IsConsentBypassed == true;
        var profile = await ResolveActiveAccessProfileAsync(query, ct).ConfigureAwait(false);
        TableAccessDecision decision;
        if (profile != null)
        {
            decision = CreateDecisionFromProfile(profile, table, query.Metadata);
        }
        else if (consentBypassed)
        {
            decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }
        else
        {
            decision = await ResolveConsentAsync(query, ct).ConfigureAwait(false);
        }

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
        SchemaContractDefinition? contract = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(metadata);

        // SEC SG-25: Fail-closed for writes without explicit write policy.
        // Consent only grants read access. If Casbin is not available or has no policies for this tenant,
        // write access is denied unless the principal is a canonical cluster admin or holds an authorized DML writer role.
        var isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(user);
        var hasDmlWriterRole = _options?.WebSql?.DmlWriterRoles != null &&
                               _options.WebSql.DmlWriterRoles.Count > 0 &&
                               _options.WebSql.DmlWriterRoles.Any(r => user.IsInRole(r));

        if (!isClusterAdmin && !hasDmlWriterRole)
        {
            if (_policyEnforcementService == null || !_policyEnforcementService.HasPolicies(tenant))
            {
                return false;
            }
        }

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
            },
            contract: contract);

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
        SchemaContractDefinition? contract = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var metadata = new TableMetadata { Identifier = table };
        return CanWriteTableAsync(user, tenant, metadata, contract, ct);
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

        var allSids = query.AllUserSids != null && query.AllUserSids.Any(s => s != query.UserSid)
            ? query.AllUserSids
            : null;
        var decision = _resolutionService.ResolveAccess(query.UserSid, query.GroupSids, query.Roles, table, activeConsents, query.Metadata.Dialect, allSids);
        if (decision == null)
        {
            return TableAccessDecision.Denied(table, "Access denied: consent resolution returned no decision.");
        }

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

    private static TableAccessDecision CreateDecisionFromProfile(AccessProfile profile, TableIdentifier table, TableMetadata metadata)
    {
        var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        if (profile.MaskingMode == MaskingPolicyMode.Unmasked)
        {
            foreach (var col in metadata.Columns)
            {
                colAccess[col.ColumnName] = ColumnAccessLevel.Clear;
            }
        }
        else if (profile.MaskingMode == MaskingPolicyMode.Default)
        {
            foreach (var col in metadata.Columns)
            {
                if (col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName))
                {
                    colAccess[col.ColumnName] = ColumnAccessLevel.Mask;
                }
                else
                {
                    colAccess[col.ColumnName] = ColumnAccessLevel.Clear;
                }
            }
        }
        else if (profile.MaskingMode == MaskingPolicyMode.Strict)
        {
            foreach (var col in metadata.Columns)
            {
                if (col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName))
                {
                    colAccess[col.ColumnName] = ColumnAccessLevel.Deny;
                }
                else
                {
                    colAccess[col.ColumnName] = ColumnAccessLevel.Clear;
                }
            }
        }

        return TableAccessDecision.Allowed(table, colAccess, rowFilterSql: profile.RowFilterPredicate, hasUnconstrainedColumnAllow: true);
    }

    private async Task<AccessProfile?> ResolveActiveAccessProfileAsync(TableAccessQuery query, CancellationToken ct)
    {
        if (_accessProfileRepository == null)
        {
            return null;
        }

        var tenant = query.Tenant;
        var subject = query.UserSid.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        IReadOnlyList<AccessProfile>? profiles = null;
        var cacheKey = $"access_profile:{tenant.Value}:{subject}";

        if (_memoryCache != null)
        {
            if (_memoryCache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is IReadOnlyList<AccessProfile> cachedProfiles)
            {
                profiles = cachedProfiles;
            }
            else
            {
                profiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenant, subject, ct).ConfigureAwait(false);
                using var entry = _memoryCache.CreateEntry(cacheKey);
                entry.Value = profiles;
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            }
        }
        else if (_profileCache.TryGetValue(cacheKey, out var entry) && entry.ExpireAt > DateTimeOffset.UtcNow)
        {
            profiles = entry.Profiles;
        }
        else
        {
            profiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenant, subject, ct).ConfigureAwait(false);
            _profileCache[cacheKey] = (profiles, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        if ((profiles == null || profiles.Count == 0) && query.AllUserSids != null && query.AllUserSids.Count > 0)
        {
            foreach (var altSid in query.AllUserSids)
            {
                if (altSid.Value == subject) continue;
                var altProfiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenant, altSid.Value, ct).ConfigureAwait(false);
                if (altProfiles.Count > 0)
                {
                    profiles = altProfiles;
                    break;
                }
            }
        }

        if (profiles == null || profiles.Count == 0)
        {
            return null;
        }

        var table = query.Metadata.Identifier;
        var now = DateTimeOffset.UtcNow;
        return profiles.FirstOrDefault(p => p.IsActive(now) && p.MatchesTable(table));
    }
}
