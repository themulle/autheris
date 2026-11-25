namespace Autheris.Application.Policy;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;

/// <summary>
/// One caller and one catalog table. <paramref name="Claims"/> feed the contract and restricted-scope checks;
/// <paramref name="ClientIp"/> and <paramref name="ExtraAttributes"/> are handed to the authorization plugin hooks.
/// <paramref name="Principal"/>(F-1) is used only to resolve caller attributes: it is never logged, hashed or put into a cache key.
/// </summary>
public sealed record TableAccessQuery(
    Sid UserSid,
    TenantId Tenant,
    IReadOnlySet<Sid> GroupSids,
    IReadOnlySet<string> Roles,
    TableMetadata Metadata,
    IEnumerable<Claim>? Claims = null,
    IReadOnlyList<string>? RequestedColumns = null,
    IPAddress? ClientIp = null,
    IReadOnlyDictionary<string, object?>? ExtraAttributes = null,
    FilterObjectKinds ObjectKind = FilterObjectKinds.Relation,
    IReadOnlySet<Sid>? AllUserSids = null,
    SchemaContractDefinition? Contract = null,
    ClaimsPrincipal? Principal = null,
    IReadOnlyList<Consent>? SupplementalConsents = null,
    EffectiveProtection? Protection = null)
{
    /// <summary>Issue #1043: batch-prefetched column classification and mask override rows (see <see cref="AccessRequest.ColumnRowPrefetch"/>).</summary>
    public Autheris.Application.Policy.Classification.ColumnPolicyRowPrefetch? ColumnRowPrefetch { get; init; }

    /// <summary>Issue #1043: batch-prefetched consents (see <see cref="AccessRequest.ConsentPrefetch"/>).</summary>
    public ConsentBatchPrefetch? ConsentPrefetch { get; init; }

    /// <summary>Batch-prefetched effective tenant access policy (see <see cref="AccessRequest.TenantPolicyPrefetch"/>).</summary>
    public TenantPolicyPrefetch? TenantPolicyPrefetch { get; init; }

    /// <summary>Issue #1047: only <see cref="TableAccessDecision.IsAllowed"/> is used (see <see cref="AccessRequest.AllowOnly"/>).</summary>
    public bool AllowOnly { get; init; }

    /// <summary>
    /// The caller's unmask coverage for this decision (set by the policy, resolved lazily and once). Every masking stage reads it, so a
    /// classification mask and a protection mask are lifted by the same unmask grants. <c>null</c> = nothing is unmasked.
    /// </summary>
    public Autheris.Application.Policy.Protection.UnmaskCoverageSource? Unmask { get; init; }

    public static TableAccessQuery ForPrincipal(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenant,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        IReadOnlyDictionary<string, object?>? extraAttributes = null,
        SchemaContractDefinition? contract = null,
        EffectiveProtection? protection = null)
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
            ClientIp: null,
            extraAttributes,
            AllUserSids: principal.GetAllUserSids(),
            Contract: contract,
            Principal: principal,
            Protection: protection);
    }
}

/// <summary>
/// Architecture 1 (SQL2-6, POL-6): the single access decision for a caller on a catalog table. REST/OData/GraphQL
/// (<see cref="Autheris.Application.Services.GatewayExecutionService"/>), WebSQL, stored procedures, DuckDB OLAP and the unified PDP all decide here, in
/// the same order:
/// <list type="number">
/// <item>consents: bypass switch, else decision cache (epoch compare-and-set) or the consent repository, tenant-filtered;</item>
/// <item>virtual filters, access profiles and the authorization plugin hooks, which can only restrict the consent decision.</item>
/// </list>
/// Callers keep what is specific to them (catalog lookup, tenant selection, audit, SQL validation of the merged filter).
/// </summary>
public sealed partial class TableAccessPolicy
{
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
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
    private readonly Autheris.Application.Policy.RowScopes.RowScopeCompiler? _rowScopeCompiler;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _catalogVersionProvider;
    private readonly IContainerOwnership? _ownershipRepository;
    private readonly Autheris.Application.Policy.Classification.ClassificationAccessResolver? _classificationResolver;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly Autheris.Application.Policy.Protection.IUnmaskGrantResolver? _unmaskResolver;
    private readonly Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? _accessPolicySettings;
    private readonly Autheris.Application.Identity.IIdentityRealmRegistry? _realmRegistry;
    private readonly TimeProvider _timeProvider;

    /// <param name="mandatoryFilters">
    /// Virtual filters; required on purpose: a decision point without it would silently skip them. Use
    /// <see cref="Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver"/> only where there are none by design.
    /// </param>
    public TableAccessPolicy(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
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
        Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        Autheris.Application.Policy.RowScopes.RowScopeCompiler? rowScopeCompiler = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? catalogVersionProvider = null,
        IContainerOwnership? ownershipRepository = null,
        TimeProvider? timeProvider = null,
        Autheris.Application.Policy.Classification.ClassificationAccessResolver? classificationResolver = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? accessPolicySettings = null,
        Autheris.Application.Identity.IIdentityRealmRegistry? realmRegistry = null,
        Autheris.Application.Policy.Protection.IUnmaskGrantResolver? unmaskResolver = null)
    {
        _unmaskResolver = unmaskResolver;
        _restrictedScopes = restrictedScopes ?? Autheris.Domain.Security.RestrictedScopePolicy.Default;
        _callerAttributes = callerAttributes;
        _rowPolicyFactory = rowPolicyFactory;
        _maskPolicyFactory = maskPolicyFactory;
        _authPluginHost = authPluginHost;
        _auditWriter = auditWriter;
        _rowScopeCompiler = rowScopeCompiler;
        _catalogVersionProvider = catalogVersionProvider;
        _ownershipRepository = ownershipRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classificationResolver = classificationResolver;
        _protectionProvider = protectionProvider;
        _accessPolicySettings = accessPolicySettings;
        _realmRegistry = realmRegistry;
        _mandatoryFilters = mandatoryFilters ?? throw new ArgumentNullException(nameof(mandatoryFilters));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
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

    /// <summary>
    /// Issue #1043: reads the column classification and mask override rows of <paramref name="tables"/> for one batch of decisions.
    /// Null when this policy does not evaluate classifications from the repository.
    /// </summary>
    public async ValueTask<Autheris.Application.Policy.Classification.ColumnPolicyRowPrefetch?> PrefetchColumnRowsAsync(
        IReadOnlyList<TableMetadata> tables,
        TenantId tenant,
        CancellationToken ct)
    {
        if (_classificationResolver is not { HasRepository: true } || string.IsNullOrWhiteSpace(tenant.Value) || _options.IsConsentBypassed)
        {
            return null;
        }

        return await _classificationResolver.PrefetchColumnRowsAsync(tables, tenant.Value, ct).ConfigureAwait(false);
    }

    public async Task<TableAccessDecision> DecideAsync(TableAccessQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var decision = await DecideCoreAsync(query, ct).ConfigureAwait(false);
        return AttachTypedMasks(decision, query);
    }

    /// <summary>
    /// The typed masks come from the final column levels, with the catalog rules of the same
    /// table. A mask that cannot be expressed as a typed spec leaves the decision string-only, which the compiler path denies
    /// (<see cref="TableAccessDecision.RequireTypedColumnMasks"/>); the string consumers are not affected.
    /// </summary>
    private TableAccessDecision AttachTypedMasks(TableAccessDecision decision, TableAccessQuery query)
    {
        if (_maskPolicyFactory == null || !decision.IsAllowed || query.Metadata.IsDelegatedSource)
        {
            return decision;
        }

        if (decision.ColumnMasks is not null && decision.ColumnMasks is not ColumnMaskPolicy)
        {
            return decision;
        }

        try
        {
            var masks = _maskPolicyFactory.ForDecision(decision, query.Metadata, query.Tenant, decision.ColumnMasks);
            return masks == null ? (decision.ColumnMasks is null ? decision : decision with { ColumnMasks = null }) : decision with { ColumnMasks = masks };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
        {
            return decision;
        }
    }

    private bool IsGroupResolutionUnavailable(TableAccessQuery query)
    {
        static bool Marks(Claim c) =>
            (string.Equals(c.Type, Autheris.Domain.Identity.RoleResolutionClaims.ClaimType, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Value, Autheris.Domain.Identity.RoleResolutionClaims.Degraded, StringComparison.OrdinalIgnoreCase)) ||
            (c.Type == Autheris.Domain.Identity.GroupResolutionClaims.StatusClaimType &&
             c.Value == Autheris.Domain.Identity.GroupResolutionClaims.DegradedValue);

        var now = _timeProvider.GetUtcNow();
        return query.Claims?.Any(Marks) == true
            || query.Principal?.Claims.Any(Marks) == true
            || (query.Principal is not null && Autheris.Domain.Identity.GroupResolutionClaims.IsExpired(query.Principal, now));
    }

    private async Task<TableAccessDecision> DecideCoreAsync(TableAccessQuery query, CancellationToken ct)
    {
        var table = query.Metadata.Identifier;

        // SG-12: Inactive tables are treated as not found / denied centrally
        if (!query.Metadata.Table.IsActive)
        {
            return TableAccessDecision.Denied(table, $"Table '{table.ToQualifiedName()}' is not active.");
        }

        // GOV-08: group and role resolution was unavailable or expired for this request, so group-scoped deny rules and consents cannot be
        // evaluated reliably. Deny the data request instead of treating the caller as a member of no group (fail closed).
        if (IsGroupResolutionUnavailable(query))
        {
            return TableAccessDecision.Denied(table, "identity.groups_unresolved: Group resolution unavailable (fail-closed).");
        }

        var consentBypassed = _options?.IsConsentBypassed == true;

        var (resolvedProtection, snapshotFailure) = await ResolveProtectionSnapshotAsync(query, required: !consentBypassed, ct).ConfigureAwait(false);
        if (snapshotFailure is not null)
        {
            return snapshotFailure;
        }

        if (resolvedProtection is not null && query.Protection is null)
        {
            query = query with { Protection = resolvedProtection };
        }

        // Enforce schema contracts at query execution time using protection classes only.
        if (CheckSchemaContract(query, _contractManager) is { } contractDenial)
        {
            return contractDenial;
        }

        IReadOnlyList<AccessProfile> profiles;
        try
        {
            profiles = await ResolveActiveAccessProfilesAsync(query, ct).ConfigureAwait(false);
        }
        catch (Autheris.Application.Policy.Exceptions.AccessProfileSourceUnavailableException ex)
        {
            return TableAccessDecision.Denied(table, $"Access profile source unavailable (fail-closed): {ex.Message}");
        }

        // Baseline and consent evaluation; deny consents and protection gates always decide before narrowing layers run.
        TableAccessDecision decision = consentBypassed
            ? TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true)
            : await EvaluateProtectionAndConsentAsync(query, resolvedProtection, ct).ConfigureAwait(false);

        // 143 (-14): a query without any claims (absent, not merely without scp) has unknown token scopes and is treated as
        // holding every restricted scope with no clearance. Applied after the cache read and the rebuilds above; the cache holds the plain
        // decision of the (scope-less) key and never this copy. Claims without scp stay unrestricted (scope-less and app-only tokens).
        if (query.Claims is null && decision.IsAllowed && RestrictedScopeClearance.Unknown(_restrictedScopes) is { } unknown)
        {
            decision = decision with { ScopeClearance = unknown };
            if (decision.PerCellColumns is { } perCell)
            {
                var restricted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in query.Metadata.Columns)
                {
                    if (ClassificationMetadata.IsColumnSensitive(query.Metadata, col) && !unknown.Permits(col.ColumnName))
                    {
                        restricted.Add(col.ColumnName);
                    }
                }

                if (restricted.Count > 0)
                {
                    decision = NarrowPerCellDecision(decision, restricted);
                }
            }
        }

        if (profiles.Count > 0 && decision.IsAllowed)
        {
            // Most restrictive masking mode of the matched profiles; the row filters of all of them are AND-ed. An invalid profile row
            // filter or conflicting parameters deny the table (fail closed).
            decision = ApplyAccessProfiles(decision, profiles, query.Metadata, query.Protection);
            if (!decision.IsAllowed)
            {
                return decision;
            }

            if (!query.Metadata.IsDelegatedSource && profiles.Any(p => p.RowScope.Count > 0))
            {
                decision = await ApplyAccessProfileRowScopesAsync(decision, query, profiles, _rowScopeCompiler, _callerAttributes, ct).ConfigureAwait(false);
                if (!decision.IsAllowed)
                {
                    return decision;
                }
            }
        }

        // Virtual filters / hard row policies: restrictive, AND with the consent decision (after the consent cache, which stays free of them;
        // also with the consent bypass, decision 1). They never turn a denial into an allow.
        if (decision.IsAllowed)
        {
            bool useCompilerForHard = _rowScopeCompiler is { CanResolveHardPolicies: true } &&
                _mandatoryFilters is Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver or Autheris.Application.VirtualFilters.MandatoryRowFilterResolver;
            decision = useCompilerForHard
                ? await ApplyCompiledHardRowPoliciesAsync(decision, query, _rowScopeCompiler!, _callerAttributes, ct).ConfigureAwait(false)
                : await ApplyMandatoryVirtualFiltersAsync(decision, query, _mandatoryFilters, _rowPolicyFactory, _callerAttributes, ct).ConfigureAwait(false);
        }

        if (!decision.IsAllowed)
        {
            return decision;
        }

        // Extension Point 2: Custom Authorization Hooks
        if (decision.IsAllowed && AuthorizationHooksActive)
        {
            decision = await ApplyAuthorizationHooksAsync(decision, query, _authPluginHost!, ct).ConfigureAwait(false);
        }

        return decision;
    }

    /// <summary>The authorization plugin hooks run on every allowed decision; they see the column levels.</summary>
    private bool AuthorizationHooksActive =>
        _options?.Plugins?.Auth is { Enabled: true, AllowAuthorizationHooks: true } && _authPluginHost is { HasAuthorizationHooks: true };

    internal static TableAccessDecision? CheckSchemaContract(TableAccessQuery query, ISchemaContractManager? contractManager)
    {
        var table = query.Metadata.Identifier;
        var contract = query.Contract;
        if (contract == null && contractManager != null && contractManager.IsEnabled)
        {
            var claimContract = query.Claims?.FirstOrDefault(c => string.Equals(c.Type, "contract", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(claimContract))
            {
                if (!contractManager.HasContract(claimContract))
                {
                    return TableAccessDecision.Denied(table,
                        $"Schema Contract Denial: Unknown schema contract '{claimContract}' specified in caller token.");
                }
                contract = contractManager.GetContract(claimContract);
            }
            else
            {
                var defaultContract = contractManager.DefaultContract;
                if (!string.IsNullOrWhiteSpace(defaultContract) && contractManager.HasContract(defaultContract))
                {
                    contract = contractManager.GetContract(defaultContract);
                }
            }
        }

        if (contract == null)
        {
            return null;
        }

        if (contract.AllowedTables.Count > 0 &&
            !contract.AllowedTables.Contains(table.ToString()) &&
            !contract.AllowedTables.Contains(table.TableName))
        {
            return TableAccessDecision.Denied(table,
                $"Schema Contract Denial: Table '{table.ToQualifiedName()}' is not permitted under contract '{contract.Name}'.");
        }

        var tableClass = (query.Protection?.TableClass ?? ProtectionClass.Confidential).NormalizeOrConfidential();

        if (contract.ExcludedClasses.Contains(tableClass))
        {
            return TableAccessDecision.Denied(table,
                $"Schema Contract Denial: Table '{table.ToQualifiedName()}' with class '{tableClass.ToCanonicalName()}' is excluded under contract '{contract.Name}'.");
        }

        if (contract.IncludedClasses.Count > 0 && !contract.IncludedClasses.Contains(tableClass))
        {
            return TableAccessDecision.Denied(table,
                $"Schema Contract Denial: Table '{table.ToQualifiedName()}' with class '{tableClass.ToCanonicalName()}' is not in included classes under contract '{contract.Name}'.");
        }

        return null;
    }

    internal static async Task<TableAccessDecision> ApplyMandatoryVirtualFiltersAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver mandatoryFilters,
        RowPolicyFactory? rowPolicyFactory,
        Autheris.Application.Identity.Attributes.ICallerAttributeResolver? callerAttributes,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var mandatory = await mandatoryFilters.ResolveAsync(
            new Autheris.Application.VirtualFilters.MandatoryFilterQuery(query.UserSid, query.GroupSids, query.Roles, query.Tenant, query.Metadata, query.ObjectKind, query.AllUserSids),
            ct).ConfigureAwait(false);
        if (mandatory.IsDenied)
        {
            return TableAccessDecision.Denied(table, mandatory.DenyReason ?? "Denied by virtual filters.");
        }

        if (mandatory.PredicateSql == null)
        {
            return decision;
        }

        if (mandatory.CallerAttributesTextConstant && (rowPolicyFactory == null || mandatory.TypedPredicate == null))
        {
            return TableAccessDecision.Denied(table, "Denied by virtual filters.");
        }

        if (rowPolicyFactory != null && mandatory.TypedPredicate != null)
        {
            var typed = mandatory.TypedPredicate;
            IReadOnlyList<CallerAttributeAuditEntry>? audit = null;
            if (mandatory.CallerAttributes.Count > 0)
            {
                (typed, audit) = await BindVirtualFilterAttributesCoreAsync(typed, mandatory.CallerAttributes, query, callerAttributes, ct).ConfigureAwait(false);
            }

            decision = decision.WithMandatoryPredicate(rowPolicyFactory.FromPredicate(typed, query.Metadata.Dialect), mandatory.AppliedFilters, mandatory.AppliedBindings);
            if (audit is { Count: > 0 })
            {
                decision = decision with { CallerAttributes = Merge(decision.CallerAttributes, audit) };
            }
        }
        else
        {
            decision = decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings);
        }

        return decision;
    }

    internal static async Task<TableAccessDecision> ApplyAuthorizationHooksAsync(
        TableAccessDecision decision,
        TableAccessQuery query,
        IAuthPluginHost authPluginHost,
        CancellationToken ct)
    {
        var table = query.Metadata.Identifier;
        var authzContext = new PluginAuthorizationContext
        {
            TenantId = query.Tenant,
            UserSid = query.UserSid,
            GroupSids = query.GroupSids,
            Roles = query.Roles,
            TableIdentifier = table,
            RequestedColumns = query.RequestedColumns,
            TableClass = (query.Protection?.TableClass ?? ProtectionClass.Confidential).NormalizeOrConfidential(),
            ClientIp = query.ClientIp,
            ExtraAttributes = query.ExtraAttributes
        };

        var hookResult = await authPluginHost.EvaluateAuthorizationHooksAsync(authzContext, ct).ConfigureAwait(false);
        if (hookResult.Outcome == PluginAuthorizationOutcome.Deny)
        {
            return TableAccessDecision.Denied(table,
                $"Plugin Authorization Denial ({hookResult.HookName ?? "Unknown"}): {hookResult.ReasonCode ?? "Access denied by enterprise plugin."}");
        }

        if (hookResult.Outcome == PluginAuthorizationOutcome.Restrict)
        {
            var restrictedColumns = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
            if (hookResult.DeniedColumns != null)
            {
                foreach (var col in hookResult.DeniedColumns)
                {
                    restrictedColumns[col] = ColumnAccessLevel.Deny;
                }
            }

            decision = decision with { ColumnAccess = restrictedColumns };

            if (!string.IsNullOrWhiteSpace(hookResult.AdditionalRowFilterSql))
            {
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

        return decision;
    }

    private static TableAccessDecision NarrowPerCellDecision(TableAccessDecision decision, IReadOnlySet<string> restrictedToMask)
    {
        if (decision.PerCellColumns is null)
        {
            return decision;
        }

        var narrowed = decision.PerCellColumns.Narrow(decision.ColumnAccess, restrictedToMask);
        if (narrowed is null)
        {
            var flattened = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
            foreach (var sig in decision.PerCellColumns.Signatures)
            {
                foreach (var (col, lvl) in sig.ColumnAccess)
                {
                    if (!flattened.TryGetValue(col, out var cur) || cur == ColumnAccessLevel.RowDependent)
                    {
                        flattened[col] = restrictedToMask.Contains(col) && lvl == ColumnAccessLevel.Clear
                            ? ColumnAccessLevel.Mask
                            : lvl;
                    }
                }
            }

            return decision with
            {
                ColumnAccess = flattened,
                RowDependentColumns = null,
                ColumnSignatures = null,
                PerCellColumns = null
            };
        }

        var updatedCols = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
        foreach (var col in updatedCols.Keys.ToList())
        {
            if (updatedCols[col] == ColumnAccessLevel.RowDependent && !narrowed.RowDependentColumns.Contains(col))
            {
                updatedCols[col] = narrowed.Signatures[0].ColumnAccess.GetValueOrDefault(col, ColumnAccessLevel.Deny);
            }
        }

        return decision with
        {
            ColumnAccess = updatedCols,
            RowDependentColumns = narrowed.RowDependentColumns,
            ColumnSignatures = narrowed.Signatures,
            PerCellColumns = narrowed
        };
    }
}
