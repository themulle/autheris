namespace Autheris.Application.ConsentRequests.Routing;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record ConsentWorkflowRouteResolution(
    ConsentWorkflowMode Mode,
    Guid? ExternalSystemId,
    int MaxDurationDays,
    int RequestTimeoutDays,
    int ExpiryNoticeLeadHours,
    bool RequireResolvableApprover,
    WorkflowRouteScopeKind? MatchedScopeKind,
    bool ConflictDetected = false);

public sealed record ConsentWorkflowTableInputs(
    IReadOnlyList<ConsentWorkflowRoute> TenantRoutes,
    IReadOnlySet<string> TableOwnerIds);

public sealed record ConsentRerouteResult(
    int ReroutedCount,
    int RejectedCount,
    IReadOnlyList<Guid> ReroutedRequestIds);

/// <summary>
/// Resolves workflow routes at request creation (<c>DATA_SOURCE -&gt; DATA_OWNER -&gt; TENANT -&gt; Internal</c>).
/// Conflicting data-owner routes fail safely back to <see cref="ConsentWorkflowMode.Internal"/> and record an audit event.
/// </summary>
public interface IConsentWorkflowRouter
{
    Task<ConsentWorkflowRouteResolution> ResolveForTableAsync(
        TenantId tenantId,
        TableIdentifier table,
        CancellationToken ct = default);

    Task<ConsentWorkflowRouteResolution> ResolveForTablesAsync(
        TenantId tenantId,
        IReadOnlyList<TableIdentifier> tables,
        CancellationToken ct = default);
}

/// <summary>
/// Persistence operations for workflow routes, registered external workflow systems, and atomic request re-routing.
/// </summary>
public interface IConsentWorkflowAdminRepository
{
    Task<ConsentWorkflowTableInputs> GetRoutingInputsForTableAsync(
        TenantId tenantId,
        TableIdentifier table,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConsentWorkflowRoute>> ListWorkflowRoutesAsync(
        TenantId tenantId,
        CancellationToken ct = default);

    Task<ConsentWorkflowRoute?> GetWorkflowRouteAsync(
        TenantId tenantId,
        WorkflowRouteScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default);

    Task<ConsentWorkflowRoute> UpsertWorkflowRouteAsync(
        ConsentWorkflowRoute route,
        CancellationToken ct = default);

    Task<bool> DeleteWorkflowRouteAsync(
        TenantId tenantId,
        WorkflowRouteScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default);

    Task<ExternalWorkflowSystem> UpsertExternalWorkflowSystemAsync(
        ExternalWorkflowSystem system,
        CancellationToken ct = default);

    Task<(ExternalWorkflowSystem? System, ConsentRerouteResult RerouteResult)> DisableExternalWorkflowSystemAsync(
        TenantId tenantId,
        Guid systemId,
        string actorSid,
        DateTimeOffset disabledAt,
        CancellationToken ct = default);

    Task<ConsentRerouteResult> RerouteOpenRequestsAsync(
        TenantId tenantId,
        Guid? fromSystemId,
        ConsentWorkflowMode? fromMode,
        ConsentWorkflowMode targetMode,
        Guid? targetSystemId,
        string actorSid,
        DateTimeOffset occurredAt,
        IReadOnlyList<Guid>? requestIds = null,
        CancellationToken ct = default);
}
