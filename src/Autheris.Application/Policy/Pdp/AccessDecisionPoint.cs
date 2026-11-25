namespace Autheris.Application.Policy.Pdp;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed partial class AccessDecisionPoint : IAccessDecisionPoint
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IConsentCacheService? _cacheService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IMandatoryRowFilterResolver _mandatoryFilters;
    private readonly ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly IMemoryCache? _memoryCache;
    private readonly IAccessProfileCache? _accessProfileCache;
    private readonly TypedPolicySources? _typedPolicies;
    private readonly IRestrictedScopePolicy? _restrictedScopes;
    private readonly IAuthPluginHost? _authPluginHost;
    private readonly IAuditWriter? _auditWriter;
    private readonly ILogger _logger;
    private readonly TableAccessPolicy? _policy;
    private readonly Autheris.Application.Policy.WriteGrants.IWriteGrantEvaluator? _writeGrantEvaluator;
    private readonly TimeProvider _timeProvider;
    private readonly Autheris.Application.Policy.RowScopes.RowScopeCompiler? _rowScopeCompiler;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _catalogVersionProvider;
    private readonly IContainerOwnership? _ownershipRepository;
    private readonly Autheris.Application.Products.IDataProductRepository? _productRepository;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? _accessPolicySettings;
    private readonly Autheris.Application.Identity.IIdentityRealmRegistry? _realmRegistry;
    private readonly Autheris.Application.Policy.Protection.IUnmaskGrantResolver? _unmaskResolver;

    public AccessDecisionPoint(
        IOptions<GatewayOptions> options,
        IConsentResolutionService? resolutionService = null,
        IConsentRepository? consentRepository = null,
        IConsentCacheService? cacheService = null,
        IClientIpResolver? clientIpResolver = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null,
        IAccessProfileCache? accessProfileCache = null,
        TypedPolicySources? typedPolicies = null,
        IRestrictedScopePolicy? restrictedScopes = null,
        IAuthPluginHost? authPluginHost = null,
        IAuditWriter? auditWriter = null,
        ILogger<AccessDecisionPoint>? logger = null,
        Autheris.Application.Policy.WriteGrants.IWriteGrantEvaluator? writeGrantEvaluator = null,
        Autheris.Application.Policy.WriteGrants.IRowPolicyDependencyIndex? policyDependencyIndex = null,
        TimeProvider? timeProvider = null,
        Autheris.Application.Policy.RowScopes.RowScopeCompiler? rowScopeCompiler = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? catalogVersionProvider = null,
        IContainerOwnership? ownershipRepository = null,
        Autheris.Application.Products.IDataProductRepository? productRepository = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? accessPolicySettings = null,
        Autheris.Application.Identity.IIdentityRealmRegistry? realmRegistry = null,
        Autheris.Application.Policy.Protection.IUnmaskGrantResolver? unmaskResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectionProvider = protectionProvider;
        _accessPolicySettings = accessPolicySettings;
        _realmRegistry = realmRegistry;
        _resolutionService = resolutionService;
        _consentRepository = consentRepository;
        _cacheService = cacheService ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance;
        _clientIpResolver = clientIpResolver;
        _mandatoryFilters = mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance;
        _contractManager = contractManager;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        _accessProfileCache = accessProfileCache;
        _typedPolicies = typedPolicies;
        _restrictedScopes = restrictedScopes;
        _authPluginHost = authPluginHost;
        _auditWriter = auditWriter;
        _logger = logger ?? NullLogger<AccessDecisionPoint>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rowScopeCompiler = rowScopeCompiler;
        _catalogVersionProvider = catalogVersionProvider;
        _ownershipRepository = ownershipRepository;
        _productRepository = productRepository;
        _unmaskResolver = unmaskResolver;
        _writeGrantEvaluator = writeGrantEvaluator ?? (_consentRepository != null
            ? new Autheris.Application.Policy.WriteGrants.WriteGrantEvaluator(_consentRepository, policyDependencyIndex, _timeProvider)
            : null);

        if (_consentRepository != null && _resolutionService != null)
        {
            _policy = new TableAccessPolicy(
                _consentRepository,
                _resolutionService,
                _cacheService,
                _options.Value,
                _mandatoryFilters,
                _contractManager,
                _accessProfileRepository,
                _memoryCache,
                _accessProfileCache,
                _typedPolicies?.RowPolicies,
                _typedPolicies?.MaskPolicies,
                _typedPolicies?.CallerAttributes,
                _restrictedScopes,
                _authPluginHost,
                _auditWriter,
                _rowScopeCompiler,
                _catalogVersionProvider,
                _ownershipRepository,
                _timeProvider,
                _typedPolicies?.ClassificationResolver,
                _protectionProvider,
                _accessPolicySettings,
                _realmRegistry,
                unmaskResolver);
        }
    }

    public async ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject;
        ArgumentNullException.ThrowIfNull(request.Object);
        var metadata = WithConnectionEnforcementMode(request.Object, _options.Value);
        if (!ReferenceEquals(metadata, request.Object))
        {
            request = request with { Object = metadata };
        }

        var table = metadata.Identifier;
        bool consentBypassed = _options.Value.IsConsentBypassed;

        if (subject is null
            || (!subject.IsAuthenticated && !consentBypassed)
            || (subject.IsAuthenticated && string.IsNullOrWhiteSpace(subject.UserSid.Value)))
        {
            return PreconditionDenied(table, "IDENTITY_INCOMPLETE", "Identity", null, "Access denied: caller identity is incomplete.");
        }

        if (subject.TenantClaimInvalid
            || subject.ResolvedTenant is not { Value: { Length: > 0 } resolvedTenant }
            || string.IsNullOrWhiteSpace(resolvedTenant))
        {
            return PreconditionDenied(table, "TENANT_UNRESOLVED", "Identity", null, "Access denied: tenant could not be resolved.");
        }

        var now = _timeProvider.GetUtcNow();
        if ((subject.IsAuthenticated && subject.GroupResolutionStatus != GroupResolutionStatus.Complete)
            || (!subject.IsAuthenticated && !consentBypassed && subject.GroupResolutionStatus != GroupResolutionStatus.Complete)
            || (subject.Principal != null && GroupResolutionClaims.IsMarkedDegraded(subject.Principal))
            || GroupResolutionClaims.HasAnyDegradedClaim(subject.Claims)
            || (subject.GroupsExpireAt is { } expiresAt && expiresAt <= now)
            || (subject.Principal != null && GroupResolutionClaims.IsExpired(subject.Principal, now)))
        {
            return PreconditionDenied(table, "GROUPS_DEGRADED", "Identity", null, "identity.groups_unresolved: Group resolution unavailable (fail-closed).");
        }

        if (!metadata.Table.IsActive)
        {
            return PreconditionDenied(table, "TABLE_INACTIVE", "Catalog", null, $"Table '{table.ToQualifiedName()}' is not active.");
        }

        var isProduct = Autheris.Application.Products.ProductSource.IsProductSource(table.Domain);
        if (isProduct && request.Operation is AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete)
        {
            return PreconditionDenied(table, "PRODUCT_READ_ONLY", "Product", null, "Access denied: a data product is read-only.");
        }

        if (isProduct && !Autheris.Application.Products.ProductSource.TryParse(table, subject.TenantId, out _, out _))
        {
            // the identity must name a product of the caller's own tenant
            return PreconditionDenied(table, "PRODUCT_UNKNOWN", "Product", null, "Access denied: the data product is not available.");
        }

        if (request.Operation == AccessOperation.ExecuteWrite)
        {
            return isProduct
                ? await DecideExecuteWriteAsync(request, ct).ConfigureAwait(false)
                : PreconditionDenied(table, "EXECUTE_WRITE_NOT_PRODUCT", "Product", null, "Access denied: write execution is only available on data products.");
        }

        if (request.Operation is AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete)
        {
            return await DecideWriteAsync(request, ct).ConfigureAwait(false);
        }

        var roles = MergeRoles(subject.TenantRoles, subject.ClusterRoles);

        if (_policy != null)
        {
            var clientIp = subject.ClientIp ?? _clientIpResolver?.ResolveClientIp() ?? IPAddress.None;
            var isProcedure = request.Transport == AccessTransport.Procedure;

            var principal = subject.Principal;

            // A batch-resolved protection snapshot (fresh, same tenant) spares the per-table snapshot load; otherwise the policy loads it.
            EffectiveProtection? batchProtection = request.ProtectionPrefetch is { } protectionPrefetch &&
                protectionPrefetch.TryGet(subject.TenantId, metadata.Identifier, _timeProvider.GetUtcNow(), out var prefetchedProtection)
                    ? prefetchedProtection
                    : null;

            var query = new TableAccessQuery(
                subject.UserSid,
                subject.TenantId,
                subject.GroupSids,
                roles,
                metadata,
                Claims: subject.Claims,
                RequestedColumns: request.Columns,
                ClientIp: clientIp,
                ExtraAttributes: isProcedure ? new Dictionary<string, object?> { ["gql.action"] = "read" } : null,
                ObjectKind: isProcedure ? FilterObjectKinds.ProcedureResult : FilterObjectKinds.Relation,
                AllUserSids: isProcedure ? null : (principal?.GetAllUserSids() ?? [subject.UserSid]),
                Principal: principal,
                SupplementalConsents: request.SupplementalConsents,
                Protection: batchProtection)
            {
                ColumnRowPrefetch = request.ColumnRowPrefetch,
                ConsentPrefetch = request.ConsentPrefetch,
                TenantPolicyPrefetch = request.TenantPolicyPrefetch,
                AllowOnly = request.AllowOnly
            };

            var decision = await _policy.DecideAsync(query, ct).ConfigureAwait(false);
            if (isProduct)
            {
                // Definer semantics: the policy above only looked at grants, denies and hard row policies of the product object itself.
                decision = await ApplyProductColumnClassesAsync(decision, subject.TenantId, ct).ConfigureAwait(false);
            }
            if (metadata.Table.ObjectKind == DiscoveryObjectKind.MetricView)
            {
                decision = ApplyMetricViewMeasureDefaults(decision, metadata);
            }
            if ((isProduct || metadata.Table.ObjectKind == DiscoveryObjectKind.MetricView) &&
                decision.IsAllowed &&
                _typedPolicies?.MaskPolicies != null)
            {
                decision = decision with { ColumnMasks = _typedPolicies.MaskPolicies.ForDecision(decision, metadata, subject.TenantId, decision.ColumnMasks) };
            }

            return decision with { EvaluatedByPdp = true };
        }

        if (consentBypassed)
        {
            var allowed = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
            if (_rowScopeCompiler is { CanResolveHardPolicies: true } &&
                _mandatoryFilters is NullMandatoryRowFilterResolver or MandatoryRowFilterResolver)
            {
                var hardQuery = new TableAccessQuery(
                    subject.UserSid,
                    subject.TenantId,
                    subject.GroupSids,
                    roles,
                    metadata,
                    Claims: subject.Claims,
                    RequestedColumns: request.Columns,
                    ObjectKind: request.Transport == AccessTransport.Procedure ? FilterObjectKinds.ProcedureResult : FilterObjectKinds.Relation,
                    AllUserSids: subject.Principal?.GetAllUserSids() ?? [subject.UserSid],
                    Principal: subject.Principal);
                allowed = await TableAccessPolicy.ApplyCompiledHardRowPoliciesAsync(
                    allowed,
                    hardQuery,
                    _rowScopeCompiler,
                    _typedPolicies?.CallerAttributes,
                    ct).ConfigureAwait(false);
                if (!allowed.IsAllowed)
                {
                    return allowed with { EvaluatedByPdp = true };
                }
            }
            else
            {
                var mandatory = await _mandatoryFilters.ResolveAsync(
                    new MandatoryFilterQuery(subject.UserSid, subject.GroupSids, roles, subject.TenantId, metadata),
                    ct).ConfigureAwait(false);
                if (mandatory.IsDenied)
                {
                    return TableAccessDecision.Denied(table, mandatory.DenyReason ?? "Denied by virtual filters.") with { EvaluatedByPdp = true };
                }
                if (mandatory.PredicateSql != null)
                {
                    allowed = allowed.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings);
                }
            }
            if (isProduct)
            {
                allowed = await ApplyProductColumnClassesAsync(allowed, subject.TenantId, ct).ConfigureAwait(false);
            }
            if (metadata.Table.ObjectKind == DiscoveryObjectKind.MetricView)
            {
                allowed = ApplyMetricViewMeasureDefaults(allowed, metadata);
            }
            if (_typedPolicies?.MaskPolicies != null && allowed.IsAllowed)
            {
                allowed = allowed with { ColumnMasks = _typedPolicies.MaskPolicies.ForDecision(allowed, metadata, subject.TenantId) };
            }

            return allowed with { EvaluatedByPdp = true };
        }

        return TableAccessDecision.Denied(table, $"Access to table '{table.ToQualifiedName()}' denied by policy (policy services unavailable).") with
        {
            Reasons = [new DecisionReason(DecisionStep.DefaultDeny, "POLICY_UNAVAILABLE", "Policy", null)],
            EvaluatedByPdp = true
        };
    }

    /// <summary>
    /// A <c>PiiMasked</c> product column is always masked, a <c>PiiClearEligible</c> column is clear only with a clear rule on the product
    /// grant (the policy decision). Without a readable product the decision is denied (fail closed).
    /// </summary>
    private async ValueTask<TableAccessDecision> ApplyProductColumnClassesAsync(TableAccessDecision decision, TenantId tenant, CancellationToken ct)
    {
        if (!decision.IsAllowed)
        {
            return decision;
        }

        var product = _productRepository is null || !Autheris.Application.Products.ProductSource.TryParse(decision.Table, tenant, out var productSchema, out var productName)
            ? null
            : await _productRepository.GetProductAsync(tenant, productSchema, productName, ct).ConfigureAwait(false);
        if (product is null)
        {
            return TableAccessDecision.Denied(decision.Table, "Access denied: the data product is not available.") with { EvaluatedByPdp = true };
        }

        if (product.Kind == DataProductKind.Procedure)
        {
            // The columns of a procedure product are the declared outputs of the procedure; the procedure execution classifies them.
            return decision;
        }

        var classes = product.Columns.ToDictionary(static c => c.Name, static c => c.Class, StringComparer.OrdinalIgnoreCase);
        var access = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
        var tightened = false;
        foreach (var (column, level) in decision.ColumnAccess)
        {
            // an unknown column is treated as masked-class: it never reaches clear
            var resolved = Autheris.Application.Products.ProductColumnAccess.Resolve(
                classes.TryGetValue(column, out var cls) ? cls : ProductColumnClass.PiiMasked, level);
            if (resolved != level)
            {
                access[column] = resolved;
                tightened = true;
            }
        }

        // Under unconstrained column allow (e.g. consent bypass or ColumnSetMode.All), unlisted PiiMasked columns must still be masked.
        foreach (var column in product.Columns)
        {
            var current = access.TryGetValue(column.Name, out var existing)
                ? existing
                : (decision.HasUnconstrainedColumnAllow ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny);
            var resolved = Autheris.Application.Products.ProductColumnAccess.Resolve(column.Class, current);
            if (resolved != current)
            {
                access[column.Name] = resolved;
                tightened = true;
            }
        }

        return tightened
            ? decision with { ColumnAccess = access, PerCellColumns = null, ColumnSignatures = null, RowDependentColumns = null }
            : decision;
    }

    /// <summary>
    /// Issue #916 (D-MV-6): on a metric view, measure columns and undeclared-role columns default to Deny unless an explicit
    /// column-level grant allows the measure. Object grants alone expose dimensions only.
    /// </summary>
    internal static TableAccessDecision ApplyMetricViewMeasureDefaults(TableAccessDecision decision, TableMetadata metadata)
    {
        if (!decision.IsAllowed || metadata.Table.ObjectKind != DiscoveryObjectKind.MetricView)
        {
            return decision;
        }

        var access = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var col in metadata.Columns)
        {
            if (col.Role is null)
            {
                access[col.ColumnName] = ColumnAccessLevel.Deny;
                changed = true;
            }
            else if (col.Role == MetricViewColumnRole.Measure &&
                     (!decision.ColumnAccess.TryGetValue(col.ColumnName, out var explicitLevel) || explicitLevel == ColumnAccessLevel.Deny))
            {
                access[col.ColumnName] = ColumnAccessLevel.Deny;
                changed = true;
            }
        }

        return changed ? decision with { ColumnAccess = access } : decision;
    }

    private static readonly IReadOnlySet<string> NoRoles =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Case-insensitive union of tenant and cluster roles without LINQ and without growth rehashing. A subject without roles shares one
    /// immutable empty set (no allocation).
    /// </summary>
    internal static IReadOnlySet<string> MergeRoles(IReadOnlySet<string> tenantRoles, IReadOnlySet<string> clusterRoles)
    {
        int total = tenantRoles.Count + clusterRoles.Count;
        if (total == 0)
        {
            return NoRoles;
        }

        var merged = new HashSet<string>(total, StringComparer.OrdinalIgnoreCase);
        foreach (var role in tenantRoles)
        {
            merged.Add(role);
        }

        foreach (var role in clusterRoles)
        {
            merged.Add(role);
        }

        return merged;
    }

    internal static TableMetadata WithConnectionEnforcementMode(TableMetadata metadata, GatewayOptions options)
    {
        if (metadata.Table.EnforcementMode == EnforcementMode.AutherisEnforced &&
            Autheris.Application.Sql.Services.SqlDataSourceResolver.ResolveConnectionOptions(options, metadata.Identifier.Domain) is
                { EnforcementMode: not EnforcementMode.AutherisEnforced } conn)
        {
            return metadata with { Table = metadata.Table with { EnforcementMode = conn.EnforcementMode } };
        }

        return metadata;
    }

    private static TableAccessDecision PreconditionDenied(
        TableIdentifier table,
        string code,
        string sourceKind,
        string? sourceId,
        string message) =>
        TableAccessDecision.Denied(table, message) with
        {
            Reasons = [new DecisionReason(DecisionStep.Precondition, code, sourceKind, sourceId)],
            EvaluatedByPdp = true
        };

    public async ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> objects,
        AccessOperation operation,
        AccessTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(objects);

        return (await DecideOperationsManyAsync(subject, objects, [operation], transport, ct).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// <see cref="IAccessDecisionPoint.DecideOperationsManyAsync"/>:
    /// issue #1043 reads the caller's consents and the column classification / mask override rows once per block of objects instead of once
    /// or twice per decision; issue #1047 shares that read between the operations (a discovery page decides Discover, Insert, Update and
    /// Delete). Blocks bound the memory of a request and keep every prefetch well inside its MaxAge.
    /// </summary>
    public async ValueTask<IReadOnlyList<IReadOnlyList<TableAccessDecision>>> DecideOperationsManyAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> objects,
        IReadOnlyList<AccessOperation> operations,
        AccessTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(operations);

        var results = operations.Select(_ => new List<TableAccessDecision>(objects.Count)).ToList();
        for (var offset = 0; offset < objects.Count; offset += PrefetchBlockSize)
        {
            var block = objects.Skip(offset).Take(PrefetchBlockSize).ToList();
            var (rowPrefetch, consentPrefetch, policyPrefetch, protectionPrefetch) = await PrefetchBlockAsync(subject, block, operations, ct).ConfigureAwait(false);
            for (var op = 0; op < operations.Count; op++)
            {
                foreach (var obj in block)
                {
                    var req = new AccessRequest(subject, obj, operations[op], transport)
                    {
                        ColumnRowPrefetch = rowPrefetch,
                        ConsentPrefetch = consentPrefetch,
                        TenantPolicyPrefetch = policyPrefetch,
                        ProtectionPrefetch = protectionPrefetch
                    };
                    results[op].Add(await DecideAsync(req, ct).ConfigureAwait(false));
                }
            }
        }

        return results;
    }

    /// <summary>Objects decided with one prefetch (issue #1043): bounds the prefetched rows per request and their age.</summary>
    internal const int PrefetchBlockSize = 500;

    private async ValueTask<(
        Autheris.Application.Policy.Classification.ColumnPolicyRowPrefetch?,
        Autheris.Application.Policy.ConsentBatchPrefetch?,
        Autheris.Application.Policy.TenantPolicyPrefetch?,
        Autheris.Application.Policy.ProtectionBatchPrefetch?)> PrefetchBlockAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> block,
        IReadOnlyList<AccessOperation> operations,
        CancellationToken ct)
    {
        // Write execution reads neither. Only read decisions reach the table policy and its column rows; the Insert / Update / Delete
        // decisions read the caller's consents of the table with the same per-table read (issue #1047).
        if (_policy == null || block.Count < 2 || operations.Contains(AccessOperation.ExecuteWrite))
        {
            return (null, null, null, null);
        }

        var hasReadOperation = operations.Any(static op => op is not (AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete));
        var rowPrefetch = hasReadOperation
            ? await _policy.PrefetchColumnRowsAsync(block, subject.TenantId, ct).ConfigureAwait(false)
            : null;

        if (!subject.IsAuthenticated || string.IsNullOrWhiteSpace(subject.TenantId.Value))
        {
            return (rowPrefetch, null, null, null);
        }

        var consentPrefetch = hasReadOperation
            ? await PrefetchConsentsAsync(subject, block, ct).ConfigureAwait(false)
            : null;
        var policyPrefetch = hasReadOperation
            ? await PrefetchTenantPolicyAsync(subject.TenantId, ct).ConfigureAwait(false)
            : null;
        var protectionPrefetch = await PrefetchProtectionAsync(subject.TenantId, block, ct).ConfigureAwait(false);

        return (rowPrefetch, consentPrefetch, policyPrefetch, protectionPrefetch);
    }

    /// <summary>
    /// Issue #1047: allow-only Discover decisions (see <see cref="IAccessDecisionPoint.DecideAllowedManyAsync"/>). The caller's consents,
    /// tenant access policy, and protection snapshots are read once for the run instead of once per block, and refreshed for the
    /// remaining objects before the prefetch reaches half of <see cref="Autheris.Application.Policy.ConsentBatchPrefetch.MaxAge"/>.
    /// No column rows are prefetched: an allow-only decision skips the classification stage unless that stage can turn the allow into
    /// a deny (then it reads them itself).
    /// </summary>
    public async ValueTask<IReadOnlyList<bool>> DecideAllowedManyAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> objects,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(objects);

        var allowed = new List<bool>(objects.Count);
        Autheris.Application.Policy.ConsentBatchPrefetch? consents = null;
        Autheris.Application.Policy.TenantPolicyPrefetch? policyPrefetch = null;
        Autheris.Application.Policy.ProtectionBatchPrefetch? protectionPrefetch = null;
        var consentPrefetchEnabled = _policy != null && objects.Count > 1;
        var gatePrefetchEnabled = _policy != null && objects.Count > 1 && !string.IsNullOrWhiteSpace(subject.TenantId.Value);
        var sinceLoad = 0;
        var maxHalfAge = Autheris.Application.Policy.ConsentBatchPrefetch.MaxAge / 2;
        foreach (var obj in objects)
        {
            var now = _timeProvider.GetUtcNow();
            // Re-read for the remaining objects before the prefetch reaches half its MaxAge, but at most once per block: a slow read must
            // not turn into one read per object (decisions after MaxAge fall back to the per-table read, which is correct).
            if (consentPrefetchEnabled && (consents is null || (sinceLoad >= PrefetchBlockSize && now - consents.ReadAt >= maxHalfAge)))
            {
                consents = await PrefetchConsentsAsync(subject, objects.Skip(allowed.Count).ToList(), ct).ConfigureAwait(false);
                consentPrefetchEnabled = consents is not null; // no prefetch for this subject (preconditions, bypass): never ask again
                sinceLoad = 0;
            }

            if (gatePrefetchEnabled && (policyPrefetch is null || (sinceLoad >= PrefetchBlockSize && now - policyPrefetch.ReadAt >= maxHalfAge)))
            {
                policyPrefetch = await PrefetchTenantPolicyAsync(subject.TenantId, ct).ConfigureAwait(false);
                if (policyPrefetch is null)
                {
                    gatePrefetchEnabled = false;
                }
            }

            if (gatePrefetchEnabled && (protectionPrefetch is null || (sinceLoad >= PrefetchBlockSize && now - protectionPrefetch.ReadAt >= maxHalfAge)))
            {
                protectionPrefetch = await PrefetchProtectionAsync(subject.TenantId, objects.Skip(allowed.Count).ToList(), ct).ConfigureAwait(false);
            }

            sinceLoad++;

            var request = new AccessRequest(subject, obj, AccessOperation.Discover, AccessTransport.Discovery)
            {
                ConsentPrefetch = consents,
                TenantPolicyPrefetch = policyPrefetch,
                ProtectionPrefetch = protectionPrefetch,
                AllowOnly = true
            };
            allowed.Add((await DecideAsync(request, ct).ConfigureAwait(false)).IsAllowed);
        }

        return allowed;
    }

    private async ValueTask<Autheris.Application.Policy.TenantPolicyPrefetch?> PrefetchTenantPolicyAsync(
        TenantId tenantId,
        CancellationToken ct)
    {
        if (_accessPolicySettings is null || string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return null;
        }

        try
        {
            return await Autheris.Application.Policy.TenantPolicyPrefetch.LoadAsync(
                _accessPolicySettings, tenantId, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<Autheris.Application.Policy.ProtectionBatchPrefetch?> PrefetchProtectionAsync(
        TenantId tenantId,
        IReadOnlyList<TableMetadata> block,
        CancellationToken ct)
    {
        if (_protectionProvider is null || string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return null;
        }

        try
        {
            return await Autheris.Application.Policy.ProtectionBatchPrefetch.LoadAsync(
                _protectionProvider, tenantId, block, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<Autheris.Application.Policy.ConsentBatchPrefetch?> PrefetchConsentsAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> objects,
        CancellationToken ct)
    {
        // Consents only for subjects the preconditions of DecideAsync let through (others are denied without reading consents).
        if (_consentRepository == null ||
            _options.Value.IsConsentBypassed ||
            !subject.IsAuthenticated ||
            string.IsNullOrWhiteSpace(subject.UserSid.Value) ||
            subject.GroupResolutionStatus != GroupResolutionStatus.Complete ||
            string.IsNullOrWhiteSpace(subject.TenantId.Value))
        {
            return null;
        }

        // Every subject set a decision reads with: the token's user SIDs, the user SID and the group SIDs.
        var subjects = subject.GetEffectiveSubjectSids();
        return await Autheris.Application.Policy.ConsentBatchPrefetch.LoadAsync(
            _consentRepository, subjects, objects.Select(static o => o.Identifier).ToList(), subject.TenantId, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public async ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Object);

        var effectiveObject = WithConnectionEnforcementMode(request.Object, _options.Value);
        if (!ReferenceEquals(effectiveObject, request.Object))
        {
            request = request with { Object = effectiveObject };
        }

        var decision = await DecideAsync(request, ct).ConfigureAwait(false);
        var sourceColumns = effectiveObject.Columns;
        var cols = !decision.IsAllowed
            ? new List<ColumnStateExplanation>()
            : sourceColumns.Count > 0
                ? sourceColumns
                    .Where(c => !c.DiscoveryHidden)
                    .Select(c =>
                    {
                        var rawLevel = decision.GetColumnAccess(c.ColumnName);
                        var level = rawLevel == ColumnAccessLevel.RowDependent
                            ? ColumnAccessLevel.RowDependent
                            : decision.GetEffectiveColumnAccess(c.ColumnName, request.Object);
                        var maskType = level is ColumnAccessLevel.Mask or ColumnAccessLevel.RowDependent
                            ? ResolveMaskType(c.ColumnName, decision, request.Object)
                            : null;
                        return new ColumnStateExplanation(c.ColumnName, level, maskType);
                    })
                    .Where(c => c.Level != ColumnAccessLevel.Deny)
                    .ToList()
                : decision.ColumnAccess
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Where(kv => kv.Value != ColumnAccessLevel.Deny)
                    .Select(kv => new ColumnStateExplanation(
                        kv.Key,
                        kv.Value,
                        kv.Value is ColumnAccessLevel.Mask or ColumnAccessLevel.RowDependent ? ResolveMaskType(kv.Key, decision, request.Object) : null))
                    .ToList();

        var groups = new EffectiveGroupsExplanation(
            request.Subject.GroupSids.Select(s => s.Value).OrderBy(v => v, StringComparer.Ordinal).ToList(),
            "Token",
            request.Subject.GroupResolutionStatus.ToString());

        return new AccessExplanation(
            decision.IsAllowed,
            decision.Reasons,
            cols,
            groups,
            decision.Grants,
            decision.PolicyEpoch,
            request.Subject.GroupResolutionStatus == GroupResolutionStatus.Complete);
    }

    private static string ResolveMaskType(string columnName, TableAccessDecision decision, TableMetadata metadata)
    {
        if (decision.ColumnMasks is ColumnMaskPolicy typedMasks
            && typedMasks.TryGetSpec(columnName, out var spec))
        {
            return spec.Kind.ToString();
        }

        if (metadata.ColumnMaskingRules.TryGetValue(columnName, out var rule)
            && !string.IsNullOrWhiteSpace(rule.RuleType))
        {
            return rule.RuleType;
        }

        foreach (var (key, value) in metadata.ColumnMaskingRules)
        {
            if (string.Equals(key, columnName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value.RuleType))
            {
                return value.RuleType;
            }
        }

        return "Default";
    }
}
