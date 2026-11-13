namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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
/// Evaluates Casbin ABAC environment conditions and Consent Engine
/// permissions under a single coherent pipeline and caches the authoritative decision.
/// </summary>
public sealed class UnifiedPolicyDecisionPoint : IUnifiedPolicyDecisionPoint
{
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IPolicyEnforcementService _policyEnforcementService;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<UnifiedPolicyDecisionPoint> _logger;

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver _mandatoryFilters;
    private readonly Autheris.Application.Governance.Contracts.ISchemaContractManager? _contractManager;
    private readonly TypedPolicySources? _typedPolicies;

    private readonly Autheris.Domain.Security.IRestrictedScopePolicy? _restrictedScopes;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Autheris.Application.Policy.Interfaces.IAccessProfileCache? _accessProfileCache;

    /// <param name="accessProfileRepository">Access profiles (SR-M-07); used when no <paramref name="accessProfileCache"/> is registered.</param>
    /// <param name="accessProfileCache">
    /// The shared access profile cache: Strict/Default masking and profile row filters apply on this path exactly as on the main data path.
    /// Without both, no access profile applies here.
    /// </param>
    public UnifiedPolicyDecisionPoint(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IOptions<GatewayOptions> options,
        ILogger<UnifiedPolicyDecisionPoint> logger,
        IPolicyEnforcementService policyEnforcementService,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        Autheris.Application.Governance.Contracts.ISchemaContractManager? contractManager = null,
        TypedPolicySources? typedPolicies = null,
        Autheris.Domain.Security.IRestrictedScopePolicy? restrictedScopes = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? accessProfileCache = null)
    {
        _restrictedScopes = restrictedScopes;
        _accessProfileRepository = accessProfileRepository;
        _accessProfileCache = accessProfileCache;
        _typedPolicies = typedPolicies;
        _contractManager = contractManager;
        _mandatoryFilters = mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance;
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policyEnforcementService = policyEnforcementService ?? throw new ArgumentNullException(nameof(policyEnforcementService));
    }

    public Task<TableAccessDecision> EvaluateAccessAsync(
        TableIdentifier table,
        TableMetadata metadata,
        SecurityPrincipalContext securityContext,
        IReadOnlyList<string>? requestedColumns,
        CancellationToken ct) =>
        EvaluateAccessAsync(table, metadata, securityContext, requestedColumns, supplementalConsents: null, ct);

    public async Task<TableAccessDecision> EvaluateAccessAsync(
        TableIdentifier table,
        TableMetadata metadata,
        SecurityPrincipalContext securityContext,
        IReadOnlyList<string>? requestedColumns,
        IReadOnlyList<Consent>? supplementalConsents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(securityContext);

        var pdp = new Autheris.Application.Policy.Pdp.AccessDecisionPoint(
            _options,
            _policyEnforcementService,
            _resolutionService,
            _consentRepository,
            _cacheService,
            clientIpResolver: null,
            _mandatoryFilters,
            _contractManager,
            _accessProfileRepository,
            memoryCache: null,
            _accessProfileCache,
            _typedPolicies,
            _restrictedScopes);

        var request = new Autheris.Application.Policy.Pdp.AccessRequest(
            securityContext,
            metadata,
            Autheris.Application.Policy.Pdp.AccessOperation.Read,
            Autheris.Application.Policy.Pdp.AccessTransport.Rest,
            Columns: requestedColumns,
            SupplementalConsents: supplementalConsents);

        var decision = await pdp.DecideAsync(request, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            _logger.LogWarning("Access to table {Table} denied for subject {Subject}: {Reasons}", table, securityContext.UserSid.Value, string.Join("; ", decision.DeniedReasons));
        }

        // MISC-06: provenance marker; only a decision that went through this full pipeline may be handed on pre-evaluated.
        return decision with { EvaluatedByPdp = true };
    }
}
