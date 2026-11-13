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
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class AccessDecisionPoint : IAccessDecisionPoint
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IConsentCacheService? _cacheService;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
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

    public AccessDecisionPoint(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService? policyEnforcementService = null,
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
        ILogger<AccessDecisionPoint>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyEnforcementService = policyEnforcementService;
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

        if (_consentRepository != null && _resolutionService != null && _policyEnforcementService != null)
        {
            _policy = new TableAccessPolicy(
                _consentRepository,
                _resolutionService,
                _cacheService,
                _policyEnforcementService,
                _clientIpResolver,
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
                _auditWriter);
        }
    }

    public async ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject;
        var metadata = request.Object;
        ArgumentNullException.ThrowIfNull(metadata);
        var table = metadata.Identifier;

        if (!metadata.Table.IsActive)
        {
            return TableAccessDecision.Denied(table, $"Table '{table.ToQualifiedName()}' is not active.");
        }

        if (request.Operation is AccessOperation.Insert or AccessOperation.Update or AccessOperation.Delete)
        {
            var canWrite = _policy != null && await _policy.CanWriteTableAsync(
                subject.TenantId,
                metadata,
                ct: ct).ConfigureAwait(false);

            if (!canWrite)
            {
                return TableAccessDecision.Denied(table, $"Write access to table '{table.ToQualifiedName()}' is not permitted.");
            }
        }

        bool consentBypassed = _options.Value.IsConsentBypassed;
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
            if (_typedPolicies?.MaskPolicies != null)
            {
                allowed = allowed with { ColumnMasks = _typedPolicies.MaskPolicies.ForDecision(allowed, metadata, subject.TenantId) };
            }
            return allowed with { EvaluatedByPdp = true };
        }

        return TableAccessDecision.Denied(table, $"Access to table '{table.ToQualifiedName()}' denied by policy (policy services unavailable).");
    }

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
        var decision = await DecideAsync(request, ct).ConfigureAwait(false);
        var cols = decision.ColumnAccess.Select(kv => new ColumnStateExplanation(kv.Key, kv.Value)).ToList();
        var groups = new EffectiveGroupsExplanation(
            request.Subject.GroupSids.Select(s => s.Value).ToList(),
            "Token",
            request.Subject.GroupResolutionStatus.ToString());

        return new AccessExplanation(
            decision.IsAllowed,
            decision.Reasons,
            cols,
            groups,
            decision.Grants,
            decision.PolicyEpoch);
    }
}
