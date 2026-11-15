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
/// Deterministic workflow router resolving <c>DATA_SOURCE -&gt; DATA_OWNER -&gt; TENANT -&gt; Internal</c>.
/// Conflicting data-owner routes fall back to <see cref="ConsentWorkflowMode.Internal"/> and emit <c>CONSENT_ROUTE_CONFLICT</c>.
/// </summary>
public sealed class ConsentWorkflowRouter(
    IAuditLogRepository auditRepository,
    IConsentWorkflowConfigRepository? configRepository = null,
    IConsentWorkflowAdminRepository? adminRepository = null,
    ILogger<ConsentWorkflowRouter>? logger = null,
    TimeProvider? timeProvider = null) : IConsentWorkflowRouter
{
    public const int FallbackMaxDurationDays = 60;
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
            return await EvaluateAsync(tenantId, table, inputs.TenantRoutes, inputs.TableOwnerIds, ct).ConfigureAwait(false);
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

        var distinctSources = tables
            .Select(t => t.Domain)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_adminRepository != null)
        {
            var routes = await _adminRepository.ListWorkflowRoutesAsync(tenantId, ct).ConfigureAwait(false);
            if (distinctSources.Count == 1)
            {
                var dsRoute = routes.FirstOrDefault(r =>
                    r.ScopeKind == WorkflowRouteScopeKind.DataSource &&
                    string.Equals(r.ScopeRef, distinctSources[0], StringComparison.OrdinalIgnoreCase));
                if (dsRoute is not null)
                {
                    return FromRoute(dsRoute);
                }
            }

            var tenantRoute = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
            return tenantRoute is not null ? FromRoute(tenantRoute) : DefaultInternal();
        }

        if (_configRepository != null && distinctSources.Count == 1)
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
        IReadOnlySet<string> tableOwnerIds,
        CancellationToken ct)
    {
        var dataSourceRoute = routes.FirstOrDefault(r =>
            r.ScopeKind == WorkflowRouteScopeKind.DataSource &&
            string.Equals(r.ScopeRef, table.Domain, StringComparison.OrdinalIgnoreCase));
        if (dataSourceRoute is not null)
        {
            return FromRoute(dataSourceRoute);
        }

        var ownerRoutes = routes
            .Where(r => r.ScopeKind == WorkflowRouteScopeKind.DataOwner && tableOwnerIds.Contains(r.ScopeRef))
            .ToList();

        if (ownerRoutes.Count > 0)
        {
            var distinctModes = ownerRoutes
                .Select(r => (r.Mode, SystemId: r.Mode == ConsentWorkflowMode.External ? r.ExternalSystemId : null))
                .Distinct()
                .ToList();

            if (distinctModes.Count > 1)
            {
                await RecordRouteConflictAsync(tenantId, table, ownerRoutes, ct).ConfigureAwait(false);
                var tenantFallback = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
                return new ConsentWorkflowRouteResolution(
                    ConsentWorkflowMode.Internal,
                    null,
                    tenantFallback?.MaxDurationDays ?? FallbackMaxDurationDays,
                    tenantFallback?.RequestTimeoutDays ?? FallbackRequestTimeoutDays,
                    tenantFallback?.ExpiryNoticeLeadHours ?? FallbackExpiryNoticeLeadHours,
                    tenantFallback?.RequireResolvableApprover ?? true,
                    MatchedScopeKind: null,
                    ConflictDetected: true);
            }

            var effectiveMax = ownerRoutes.Min(r => r.MaxDurationDays);
            var winner = ownerRoutes.OrderBy(r => r.MaxDurationDays).First();
            return FromRoute(winner) with { MaxDurationDays = effectiveMax };
        }

        var tenantRoute = routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
        return tenantRoute is not null ? FromRoute(tenantRoute) : DefaultInternal();
    }

    private async Task RecordRouteConflictAsync(
        TenantId tenantId,
        TableIdentifier table,
        IReadOnlyList<ConsentWorkflowRoute> conflictingRoutes,
        CancellationToken ct)
    {
        _logger?.LogWarning(
            "Conflicting data-owner workflow routes detected for tenant {TenantId} table {Table}; falling back to Internal mode.",
            tenantId.Value, table.ToString());

        var details = JsonSerializer.Serialize(new
        {
            Table = table.ToString(),
            OwnerRoutes = conflictingRoutes.Select(r => new { r.ScopeRef, Mode = r.Mode.ToSqlString(), r.ExternalSystemId })
        });

        await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _timeProvider.GetUtcNow(),
            EventType = AuditEventTypes.ConsentRouteConflict,
            ActorSid = new Sid("SYSTEM"),
            TargetTable = table.ToString(),
            Decision = "FALLBACK_INTERNAL",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = details,
            TenantId = tenantId
        }, ct).ConfigureAwait(false);
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
