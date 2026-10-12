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

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver _mandatoryFilters;
    private readonly Autheris.Application.Governance.Contracts.ISchemaContractManager? _contractManager;

    public UnifiedPolicyDecisionPoint(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IOptions<GatewayOptions> options,
        ILogger<UnifiedPolicyDecisionPoint> logger,
        IPolicyEnforcementService? policyEnforcementService = null,
        IRebacEvaluator? rebacEvaluator = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        Autheris.Application.Governance.Contracts.ISchemaContractManager? contractManager = null)
    {
        _contractManager = contractManager;
        _mandatoryFilters = mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance;
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

        // Architecture 1: the shared access decision. ReBAC applies whenever it is enabled on this path; the caller's
        // roles are its tenant and cluster roles; there are no token claims, so Casbin sees no purpose or attributes.
        var roles = new HashSet<string>(securityContext.TenantRoles.Concat(securityContext.ClusterRoles), StringComparer.OrdinalIgnoreCase);
        var query = new TableAccessQuery(
            securityContext.UserSid,
            securityContext.TenantId,
            securityContext.GroupSids,
            roles,
            metadata,
            Claims: securityContext.Claims,
            RequestedColumns: requestedColumns,
            Rebac: RebacEnforcement.WhenEnabled,
            // RV-01: unknown client IP must never satisfy loopback/internal-network allow rules (fail-closed).
            ClientIp: securityContext.ClientIp ?? IPAddress.None);

        var decision = await new TableAccessPolicy(
            _consentRepository,
            _resolutionService,
            _cacheService,
            _policyEnforcementService ?? Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null,
            _options.Value,
            _mandatoryFilters,
            _contractManager)
            .DecideAsync(query, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            _logger.LogWarning("Access to table {Table} denied for subject {Subject}: {Reasons}", table, securityContext.UserSid.Value, string.Join("; ", decision.DeniedReasons));
        }

        return decision;
    }
}
