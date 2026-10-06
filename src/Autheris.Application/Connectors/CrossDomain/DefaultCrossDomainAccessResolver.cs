namespace Autheris.Application.Connectors.CrossDomain;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Default implementation of <see cref="ICrossDomainAccessResolver"/> that resolves table access decisions
/// via <see cref="IConsentRepository"/>, <see cref="IConsentResolutionService"/>, and optional <see cref="IPolicyEnforcementService"/>.
/// </summary>
public sealed class DefaultCrossDomainAccessResolver : ICrossDomainAccessResolver
{
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly GatewayOptions? _options;

    public DefaultCrossDomainAccessResolver(
        IConsentRepository consentRepository,
        IConsentResolutionService resolutionService,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        IOptions<GatewayOptions>? options = null)
    {
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _resolutionService = resolutionService ?? throw new ArgumentNullException(nameof(resolutionService));
        _policyEnforcementService = policyEnforcementService;
        _clientIpResolver = clientIpResolver;
        _options = options?.Value;
    }

    public async Task<TableAccessDecision> ResolveAccessAsync(
        ClaimsPrincipal principal,
        TableIdentifier table,
        TableMetadata metadata,
        TenantId? tenant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(metadata);

        if (_options?.IsConsentBypassed == true)
        {
            return TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }

        if (principal.Identity?.IsAuthenticated == true)
        {
            var userSidNullable = principal.GetUserSid();
            if (userSidNullable == null)
            {
                throw new GatewayUnauthorizedException("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.");
            }
        }
        else if (_options?.IsConsentBypassed != true)
        {
            throw new GatewayUnauthorizedException("Authentication is required to query tables.");
        }

        var userSid = principal.GetUserSid() ?? new Sid("anonymous");
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var tenantId = tenant ?? principal.GetTenantId();

        var allSubjects = groupSids.Append(userSid).ToList();
        var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        activeConsents = activeConsents
            .Where(c => c.TenantId == tenantId)
            .ToList();

        var decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

        if (decision.IsAllowed && _policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenantId))
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in principal.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            // SEC H-4: Client IP must be resolved from trusted socket / proxy via IClientIpResolver.
            // Never trust client-controlled 'ip' token claims, and fail closed to IPAddress.None.
            var clientIp = _clientIpResolver?.ResolveClientIp() ?? IPAddress.None;

            var purpose = principal.FindFirst("purpose")?.Value ?? principal.FindFirst("purpose_id")?.Value;

            var secContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenantId,
                TargetTable: table,
                RequestedColumns: metadata.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: clientIp,
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: purpose,
                Attributes: attributes,
                TargetDialect: metadata.Dialect);

            var casbinDecision = await _policyEnforcementService.EvaluatePolicyAsync(secContext, ct).ConfigureAwait(false);
            if (!casbinDecision.IsAllowed)
            {
                return TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{userSid.Value}' in tenant '{tenantId.Value}'.");
            }
            else if (!string.IsNullOrWhiteSpace(casbinDecision.CombinedRowFilterSql))
            {
                var mergedFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
                    ? $"({decision.CombinedRowFilterSql}) AND ({casbinDecision.CombinedRowFilterSql})"
                    : casbinDecision.CombinedRowFilterSql;

                decision = decision with { CombinedRowFilterSql = mergedFilter };
            }
        }

        return decision;
    }
}
