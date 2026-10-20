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
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Plugins.Auth;

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
/// <paramref name="Principal"/> (REQ-GOV-129, F-1) is used only to resolve caller attributes: it is never logged, hashed or put into a cache key.
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
    SchemaContractDefinition? Contract = null,
    ClaimsPrincipal? Principal = null)
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
            Contract: contract,
            Principal: principal);
    }
}

/// <summary>
/// Architecture 1 (SQL2-6, POL-6, API-10): the single access decision for a caller on a catalog table. REST/OData/GraphQL
/// (<see cref="Autheris.Application.Services.GatewayExecutionService"/>), WebSQL, stored procedures, DuckDB OLAP and the unified PDP all decide here, in
/// the same order:
/// <list type="number">
/// <item>ReBAC <c>can_query</c> (<see cref="RebacEnforcement"/>);</item>
/// <item>consents: bypass switch, else decision cache (epoch compare-and-set) or the consent repository, tenant-filtered;</item>
/// <item>Casbin ABAC only when consent allowed and the switch is off; a deny wins, its restriction is merged by
/// <see cref="Restrict"/>.</item>
/// </list>
/// Callers keep what is specific to them (catalog lookup, tenant selection, audit, SQL validation of the merged filter).
/// </summary>
public sealed partial class TableAccessPolicy
{
    public const string RebacDeniedReason = "ReBAC Access Denied: Not authorized by relationship graph.";

    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IPolicyEnforcementService _policyEnforcementService;
    private readonly IRebacEvaluator _rebacEvaluator;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly GatewayOptions _options;
    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver _mandatoryFilters;
    private readonly RowPolicyFactory? _rowPolicyFactory;
    private readonly ColumnMaskPolicyFactory? _maskPolicyFactory;
    private readonly Autheris.Application.Identity.Attributes.ICallerAttributeResolver? _callerAttributes;
    private readonly ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Autheris.Domain.Security.IRestrictedScopePolicy _restrictedScopes;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;
    private readonly Autheris.Application.Policy.Interfaces.IAccessProfileCache? _accessProfileCache;
    private readonly IAuthPluginHost? _authPluginHost;
    private readonly Autheris.Application.Audit.IAuditWriter? _auditWriter;

    /// <param name="mandatoryFilters">
    /// Virtual filters; required on purpose: a decision point without it would silently skip them. Use
    /// <see cref="Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver"/> only where there are none by design.
    /// </param>
    public TableAccessPolicy(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IPolicyEnforcementService policyEnforcementService,
        IRebacEvaluator rebacEvaluator,
        IClientIpResolver? clientIpResolver,
        GatewayOptions options,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver mandatoryFilters,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null,
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? accessProfileCache = null,
        RowPolicyFactory? rowPolicyFactory = null,
        ColumnMaskPolicyFactory? maskPolicyFactory = null,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes = null,
        Autheris.Domain.Security.IRestrictedScopePolicy? restrictedScopes = null,
        IAuthPluginHost? authPluginHost = null,
        Autheris.Application.Audit.IAuditWriter? auditWriter = null)
    {
        _restrictedScopes = restrictedScopes ?? Autheris.Domain.Security.RestrictedScopePolicy.Default;
        _callerAttributes = callerAttributes;
        _rowPolicyFactory = rowPolicyFactory;
        _maskPolicyFactory = maskPolicyFactory;
        _authPluginHost = authPluginHost;
        _auditWriter = auditWriter;
        _mandatoryFilters = mandatoryFilters ?? throw new ArgumentNullException(nameof(mandatoryFilters));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _policyEnforcementService = policyEnforcementService ?? throw new ArgumentNullException(nameof(policyEnforcementService));
        _rebacEvaluator = rebacEvaluator ?? throw new ArgumentNullException(nameof(rebacEvaluator));
        _clientIpResolver = clientIpResolver;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        if (accessProfileCache != null)
        {
            _accessProfileCache = accessProfileCache;
        }
        else if (accessProfileRepository != null)
        {
            _accessProfileCache = new Autheris.Application.Policy.Services.AccessProfileCache(
                accessProfileRepository,
                clusterState: null,
                memoryCache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                options != null ? Microsoft.Extensions.Options.Options.Create(options) : null);
        }
    }

    public async Task<TableAccessDecision> DecideAsync(TableAccessQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var decision = await DecideCoreAsync(query, ct).ConfigureAwait(false);
        return AttachTypedMasks(decision, query);
    }

    /// <summary>
    /// WP-X0l: the typed masks come from the final column levels (after the Casbin restriction), with the catalog rules of the same
    /// table. A mask that cannot be expressed as a typed spec leaves the decision string-only, which the compiler path denies
    /// (<see cref="TableAccessDecision.RequireTypedColumnMasks"/>); the string consumers are not affected.
    /// </summary>
    private TableAccessDecision AttachTypedMasks(TableAccessDecision decision, TableAccessQuery query)
    {
        if (_maskPolicyFactory == null || !decision.IsAllowed || decision.ColumnMasks != null)
        {
            return decision;
        }

        try
        {
            var masks = _maskPolicyFactory.ForDecision(decision, query.Metadata, query.Tenant);
            return masks == null ? decision : decision with { ColumnMasks = masks };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
        {
            return decision;
        }
    }

    private async Task<TableAccessDecision> DecideCoreAsync(TableAccessQuery query, CancellationToken ct)
    {
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

        var rebacBypassed = _options?.IsRebacBypassed == true;
        var rebacEnforced = !rebacBypassed && (query.Rebac switch
        {
            RebacEnforcement.WhenEnabled => _rebacEvaluator is { IsEnabled: true },
            _ => RebacTableGate.IsEnforcedOnQueryPaths(_options)
        });

        bool rebacAllowed = false;
        if (rebacEnforced)
        {
            rebacAllowed = await RebacTableGate.IsAllowedAsync(_rebacEvaluator, query.Tenant, query.UserSid, table, ct).ConfigureAwait(false);
            if (!rebacAllowed)
            {
                return TableAccessDecision.Denied(table, RebacDeniedReason);
            }
        }

        var consentBypassed = _options?.IsConsentBypassed == true;
        AccessProfile? profile;
        try
        {
            profile = await ResolveActiveAccessProfileAsync(query, ct).ConfigureAwait(false);
        }
        catch (Autheris.Application.Policy.Exceptions.AccessProfileSourceUnavailableException ex)
        {
            return TableAccessDecision.Denied(table, $"Access profile source unavailable (fail-closed): {ex.Message}");
        }

        // SR-M-07: an access profile is a masking exemption only. Consent resolution (Deny consents, purpose limits, consent row
        // filters) always runs first; the profile is applied on top of an Allow decision and never creates one.
        TableAccessDecision decision = consentBypassed
            ? TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
            : await ResolveConsentAsync(query, ct).ConfigureAwait(false);

        // REQ-GOV-143 (SEC-GOV143-13/-14): a query without any claims (absent, not merely without scp) has unknown token scopes and is treated as
        // holding every restricted scope with no clearance. Applied after the cache read and the rebuilds above; the cache holds the plain
        // decision of the (scope-less) key and never this copy. Claims without scp stay unrestricted (scope-less and app-only tokens).
        if (query.Claims is null && decision.IsAllowed && RestrictedScopeClearance.Unknown(_restrictedScopes) is { } unknown)
        {
            decision = decision with { ScopeClearance = unknown };
        }

        if (profile != null && decision.IsAllowed)
        {
            IRowPolicy? profilePolicy = null;
            if (_rowPolicyFactory is { CanParseAdminPredicates: true } && !string.IsNullOrWhiteSpace(profile.RowFilterPredicate))
            {
                // WP-X0e: the admin-authored predicate is parsed once into a typed policy; an invalid predicate denies the table
                // (fail closed). The denial reason never echoes the predicate text.
                try
                {
                    profilePolicy = _rowPolicyFactory.FromAccessProfile(profile.RowFilterPredicate, query.Metadata);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
                {
                    return TableAccessDecision.Denied(table, "Access profile row filter is invalid (fail-closed).");
                }
            }

            try
            {
                decision = ApplyProfileToDecision(decision, profile, query.Metadata, profilePolicy);
            }
            catch (System.Security.SecurityException)
            {
                return TableAccessDecision.Denied(table, "Conflicting row-level security parameters.");
            }
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
                // WP-X0g: with a RowPolicyFactory the typed predicate is attached (values bound, passes RequireTypedRowPolicy);
                // without one, or when a builder has no typed form, the legacy text path stays until X1.
                if (mandatory.CallerAttributesTextConstant && (_rowPolicyFactory == null || mandatory.TypedPredicate == null))
                {
                    // REQ-GOV-129 (BC-129-05): the text of a caller attribute filter is only a constant; without the typed predicate the table is denied,
                    // whether or not a sibling filter has a typed form.
                    return TableAccessDecision.Denied(table, "Denied by virtual filters.");
                }

                if (_rowPolicyFactory != null && mandatory.TypedPredicate != null)
                {
                    var typed = mandatory.TypedPredicate;
                    IReadOnlyList<CallerAttributeAuditEntry>? audit = null;
                    if (mandatory.CallerAttributes.Count > 0)
                    {
                        (typed, audit) = await BindVirtualFilterAttributesAsync(typed, mandatory.CallerAttributes, query, ct).ConfigureAwait(false);
                    }

                    decision = decision.WithMandatoryPredicate(_rowPolicyFactory.FromPredicate(typed, query.Metadata.Dialect), mandatory.AppliedFilters, mandatory.AppliedBindings);
                    if (audit is { Count: > 0 })
                    {
                        decision = decision with { CallerAttributes = Merge(decision.CallerAttributes, audit) };
                    }
                }
                else
                {
                    decision = decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings);
                }
            }
        }

        if (!decision.IsAllowed)
        {
            if (rebacAllowed)
            {
                const string diagnosticNote = "(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)";
                var reasons = new List<string>(decision.DeniedReasons);
                if (!reasons.Contains(diagnosticNote))
                {
                    reasons.Add(diagnosticNote);
                }
                decision = decision with { DeniedReasons = reasons };
            }
            return decision;
        }

        if (!consentBypassed && _policyEnforcementService != null && _policyEnforcementService.HasPolicies(query.Tenant))
        {
            var policyDecision = await _policyEnforcementService.EvaluatePolicyAsync(BuildEvaluationContext(query), ct).ConfigureAwait(false);
            if (!policyDecision.IsAllowed)
            {
                return TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{query.UserSid.Value}' in tenant '{query.Tenant.Value}'.");
            }

            decision = Restrict(decision, policyDecision, query.Metadata);
        }

        // Extension Point 2: Custom Authorization Hooks
        var authOpt = _options?.Plugins?.Auth;
        if (decision.IsAllowed &&
            authOpt?.Enabled == true &&
            authOpt.AllowAuthorizationHooks &&
            _authPluginHost != null &&
            _authPluginHost.HasAuthorizationHooks)
        {
            var authzContext = new PluginAuthorizationContext
            {
                TenantId = query.Tenant,
                UserSid = query.UserSid,
                GroupSids = query.GroupSids,
                Roles = query.Roles,
                TableIdentifier = table,
                RequestedColumns = query.RequestedColumns,
                TableTags = query.Metadata.Table.Tags,
                TableSensitivity = query.Metadata.Table.Sensitivity,
                ClientIp = query.ClientIp,
                ExtraAttributes = query.ExtraAttributes
            };

            var hookResult = await _authPluginHost.EvaluateAuthorizationHooksAsync(authzContext, ct).ConfigureAwait(false);
            if (hookResult.Outcome == PluginAuthorizationOutcome.Deny)
            {
                return TableAccessDecision.Denied(table,
                    $"Plugin Authorization Denial ({hookResult.HookName ?? "Unknown"}): {hookResult.ReasonCode ?? "Access denied by enterprise plugin."}");
            }
            else if (hookResult.Outcome == PluginAuthorizationOutcome.Restrict)
            {
                var restrictedColumns = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
                if (hookResult.DeniedColumns != null)
                {
                    foreach (var col in hookResult.DeniedColumns)
                    {
                        // Invariant 3: Hook can only lower access to Deny, never elevate from Mask/Deny to Clear
                        restrictedColumns[col] = ColumnAccessLevel.Deny;
                    }
                }

                decision = decision with { ColumnAccess = restrictedColumns };

                if (!string.IsNullOrWhiteSpace(hookResult.AdditionalRowFilterSql))
                {
                    // SEC-BL08-10: AST validation and parenthesis wrapping
                    try
                    {
                        Autheris.Application.Sql.SqlSecurityValidator.ValidatePredicateSql(
                            hookResult.AdditionalRowFilterSql,
                            nameof(hookResult.AdditionalRowFilterSql),
                            allowSubqueries: false);
                    }
                    catch (Exception)
                    {
                        return TableAccessDecision.Denied(table, "AUTH_HOOK_INVALID_FILTER: Additional row filter SQL failed validation.");
                    }

                    decision = decision.WithAdditionalRowFilter($"({hookResult.AdditionalRowFilterSql.Trim()})");
                }
            }
        }

        return decision;
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
}
