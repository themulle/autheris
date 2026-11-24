namespace Autheris.Application.ConsentRequests.Routing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Deterministic workflow router resolving <c>DOMAIN -&gt; TENANT -&gt; Internal</c>. A route whose scope kind was removed
/// (data owner, data source) or is unknown is inert: it never matches, never auto-approves and is never a wildcard.
/// </summary>
public sealed class ConsentWorkflowRouter(
    IAuditLogRepository auditRepository,
    IConsentWorkflowConfigRepository? configRepository = null,
    IConsentWorkflowAdminRepository? adminRepository = null,
    ILogger<ConsentWorkflowRouter>? logger = null,
    TimeProvider? timeProvider = null,
    IProtectionSnapshotProvider? protectionProvider = null) : IConsentWorkflowRouter
{
    /// <summary>
    /// Route maximum when no route configures one: there is no compiled-in ceiling, so the route imposes no limit
    /// (<see cref="int.MaxValue"/> days) and the tenant <c>Gateway:Governance:Grants:MaxDuration</c> alone bounds the grant.
    /// </summary>
    public const int FallbackMaxDurationDays = int.MaxValue;
    public const int FallbackRequestTimeoutDays = 30;
    public const int FallbackExpiryNoticeLeadHours = 72;

    private readonly IAuditLogRepository _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
    private readonly IConsentWorkflowConfigRepository? _configRepository = configRepository;
    private readonly IConsentWorkflowAdminRepository? _adminRepository = adminRepository ?? (configRepository as IConsentWorkflowAdminRepository);
    private readonly ILogger<ConsentWorkflowRouter>? _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ConsentWorkflowRouteResolution> ResolveForTableAsync(
        TenantId tenantId,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        if (_adminRepository != null)
        {
            var inputs = await _adminRepository.GetRoutingInputsForTableAsync(tenantId, table, ct).ConfigureAwait(false);
            return await EvaluateAsync(tenantId, table, inputs.TenantRoutes, ct).ConfigureAwait(false);
        }

        if (_configRepository != null)
        {
            var route = await _configRepository.ResolveWorkflowRouteAsync(tenantId, table, ct).ConfigureAwait(false);
            return route is not null ? FromRoute(route) : DefaultInternal();
        }

        return DefaultInternal();
    }

    public async Task<ConsentWorkflowRouteResolution> ResolveForTablesAsync(
        TenantId tenantId,
        IReadOnlyList<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        if (tables == null || tables.Count == 0)
        {
            return DefaultInternal();
        }

        if (_adminRepository != null)
        {
            var routes = await _adminRepository.ListWorkflowRoutesAsync(tenantId, ct).ConfigureAwait(false);
            var domainRoute = await TryMatchDomainRouteAsync(tenantId, tables, routes, ct).ConfigureAwait(false);
            if (domainRoute is not null)
            {
                return FromRoute(domainRoute);
            }

            var tenantRoute = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
            return tenantRoute is not null ? FromRoute(tenantRoute) : DefaultInternal();
        }

        if (_configRepository != null && tables.Count == 1)
        {
            var route = await _configRepository.ResolveWorkflowRouteAsync(tenantId, tables[0], ct).ConfigureAwait(false);
            return route is not null ? FromRoute(route) : DefaultInternal();
        }

        return DefaultInternal();
    }

    private async Task<ConsentWorkflowRouteResolution> EvaluateAsync(
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyList<ConsentWorkflowRoute> routes,
        CancellationToken ct)
    {
        var domainRoute = await TryMatchDomainRouteAsync(tenantId, [table], routes, ct).ConfigureAwait(false);
        if (domainRoute is not null)
        {
            return FromRoute(domainRoute);
        }

        var tenantRoute = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
        return tenantRoute is not null ? FromRoute(tenantRoute) : DefaultInternal();
    }

    /// <summary>Matches a domain route only when every table resolves to the same governance domain; anything else (unknown scope kinds included) never matches.</summary>
    private async Task<ConsentWorkflowRoute?> TryMatchDomainRouteAsync(
        TenantId tenantId,
        IReadOnlyList<TableIdentifier> tables,
        IReadOnlyList<ConsentWorkflowRoute> routes,
        CancellationToken ct)
    {
        if (protectionProvider is null || !routes.Any(r => r.ScopeKind == WorkflowRouteScopeKind.Domain))
        {
            return null;
        }

        Guid? domainId = null;
        foreach (var table in tables)
        {
            var protection = await protectionProvider.GetAsync(tenantId, table, ct).ConfigureAwait(false);
            if (protection.Container is not { Kind: ContainerKind.Domain } container || container.Id == Guid.Empty)
            {
                return null;
            }

            if (domainId.HasValue && domainId.Value != container.Id)
            {
                return null;
            }

            domainId = container.Id;
        }

        return domainId is { } id
            ? routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Domain &&
                string.Equals(r.ScopeRef, id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static ConsentWorkflowRouteResolution FromRoute(ConsentWorkflowRoute route) =>
        new(
            route.Mode,
            route.Mode == ConsentWorkflowMode.External ? route.ExternalSystemId : null,
            route.MaxDurationDays > 0 ? route.MaxDurationDays : FallbackMaxDurationDays,
            route.RequestTimeoutDays > 0 ? route.RequestTimeoutDays : FallbackRequestTimeoutDays,
            route.ExpiryNoticeLeadHours >= 0 ? route.ExpiryNoticeLeadHours : FallbackExpiryNoticeLeadHours,
            route.RequireResolvableApprover,
            route.ScopeKind);

    private static ConsentWorkflowRouteResolution DefaultInternal() =>
        new(
            ConsentWorkflowMode.Internal,
            null,
            FallbackMaxDurationDays,
            FallbackRequestTimeoutDays,
            FallbackExpiryNoticeLeadHours,
            true,
            MatchedScopeKind: null);
}
