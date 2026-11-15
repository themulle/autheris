namespace Autheris.Application.Policy.Pdp;

using System;
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
        Autheris.Application.Policy.RowScopes.RowScopeCompiler? rowScopeCompiler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
                _rowScopeCompiler);
        }
    }

    public async ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject;
        var metadata = request.Object;
        ArgumentNullException.ThrowIfNull(metadata);
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

        if ((subject.IsAuthenticated && subject.GroupResolutionStatus != GroupResolutionStatus.Complete)
            || (!subject.IsAuthenticated && !consentBypassed && subject.GroupResolutionStatus != GroupResolutionStatus.Complete)
            || (subject.Principal != null && GroupResolutionClaims.IsMarkedDegraded(subject.Principal)))
        {
            return PreconditionDenied(table, "GROUPS_DEGRADED", "Identity", null, "identity.groups_unresolved: Group resolution unavailable (fail-closed).");
        }

        if (!metadata.Table.IsActive)
        {
            return PreconditionDenied(table, "TABLE_INACTIVE", "Catalog", null, $"Table '{table.ToQualifiedName()}' is not active.");
        }

        if (request.Operation is AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete)
        {
            return await DecideWriteAsync(request, ct).ConfigureAwait(false);
        }

        var roles = new HashSet<string>(subject.TenantRoles.Concat(subject.ClusterRoles), StringComparer.OrdinalIgnoreCase);

        if (_policy != null)
        {
            var clientIp = subject.ClientIp ?? _clientIpResolver?.ResolveClientIp() ?? IPAddress.None;
            var isProcedure = request.Transport == AccessTransport.Procedure;

            var principal = subject.Principal;

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
                SupplementalConsents: request.SupplementalConsents);

            var decision = await _policy.DecideAsync(query, ct).ConfigureAwait(false);
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
                    return TableAccessDecision.Denied(table, mandatory.DenyReason ?? "Denied by virtual filters.");
                }
                if (mandatory.PredicateSql != null)
                {
                    allowed = allowed.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings);
                }
            }
            if (_typedPolicies?.MaskPolicies != null)
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

        var results = new List<TableAccessDecision>(objects.Count);
        foreach (var obj in objects)
        {
            var req = new AccessRequest(subject, obj, operation, transport);
            results.Add(await DecideAsync(req, ct).ConfigureAwait(false));
        }
        return results;
    }

    public async ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = await DecideAsync(request, ct).ConfigureAwait(false);
        var sourceColumns = request.Object.Columns;
        var cols = sourceColumns.Count > 0
            ? sourceColumns
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
                .ToList()
            : decision.ColumnAccess
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
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
