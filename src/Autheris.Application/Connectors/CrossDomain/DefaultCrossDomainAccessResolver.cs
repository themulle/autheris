namespace Autheris.Application.Connectors.CrossDomain;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// DuckDB OLAP access decision. Validates the caller and delegates to <see cref="TableAccessPolicy"/> (Architecture 1):
/// ReBAC whenever enabled, consents with the decision cache, Casbin.
/// </summary>
public sealed class DefaultCrossDomainAccessResolver : ICrossDomainAccessResolver
{
    private readonly TableAccessPolicy _policy;
    private readonly GatewayOptions? _options;

    public DefaultCrossDomainAccessResolver(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        IOptions<GatewayOptions>? options = null,
        IConsentCacheService? consentCache = null,
        IRebacEvaluator? rebacEvaluator = null)
    {
        _options = options?.Value;
        _policy = new TableAccessPolicy(
            consentRepository ?? throw new ArgumentNullException(nameof(consentRepository)),
            resolutionService ?? throw new ArgumentNullException(nameof(resolutionService)),
            consentCache,
            policyEnforcementService,
            rebacEvaluator,
            clientIpResolver,
            _options);
    }

    public Task<TableAccessDecision> ResolveAccessAsync(
        ClaimsPrincipal principal,
        TableIdentifier table,
        TableMetadata metadata,
        TenantId? tenant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(metadata);

        if (principal.Identity?.IsAuthenticated == true)
        {
            if (principal.GetUserSid() == null)
            {
                throw new GatewayUnauthorizedException("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.");
            }
        }
        else if (_options?.IsConsentBypassed != true)
        {
            throw new GatewayUnauthorizedException("Authentication is required to query tables.");
        }

        var userSid = principal.GetUserSid() ?? new Sid("anonymous");
        var tenantId = tenant ?? principal.GetTenantId();

        // The caller resolved the catalog entry for this table; the decision is for that entry.
        var query = TableAccessQuery.ForPrincipal(principal, userSid, tenantId, metadata, rebac: RebacEnforcement.WhenEnabled);
        return _policy.DecideAsync(query, ct);
    }
}
