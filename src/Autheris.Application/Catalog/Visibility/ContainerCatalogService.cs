using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Catalog.Visibility;

/// <summary>A container tile: what a caller sees of a business domain or data product before any table is listed.</summary>
public sealed record ContainerTile(
    string Kind,
    Guid Id,
    string Name,
    string? Description,
    string? Contact,
    string Visibility,
    string AccessState);

/// <summary>Lists container tiles with the caller's catalog access state (<c>OPEN</c>, <c>GRANTED</c>, <c>PENDING</c>, <c>NONE</c>).</summary>
public interface IContainerCatalogService
{
    ValueTask<IReadOnlyList<ContainerTile>> ListAsync(
        TenantId tenant, ClaimsPrincipal principal, ContainerKind? kind, int? limit, CancellationToken ct = default);

    ValueTask<ContainerTile?> GetAsync(
        TenantId tenant, ClaimsPrincipal principal, ContainerKind kind, Guid id, CancellationToken ct = default);
}

public sealed class ContainerCatalogService(
    IContainerVisibilitySnapshotProvider snapshots,
    IConsentApprovalRepository? approvals = null,
    int maxPageSize = ContainerCatalogService.DefaultMaxPageSize) : IContainerCatalogService
{
    public const int DefaultMaxPageSize = 500;
    private const int DefaultPageSize = 100;

    private readonly IContainerVisibilitySnapshotProvider _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly int _maxPageSize = Math.Clamp(maxPageSize, 1, 5000);

    public async ValueTask<IReadOnlyList<ContainerTile>> ListAsync(
        TenantId tenant, ClaimsPrincipal principal, ContainerKind? kind, int? limit, CancellationToken ct = default)
    {
        var page = Math.Clamp(limit ?? DefaultPageSize, 1, _maxPageSize);
        var snapshot = await _snapshots.GetAsync(tenant, principal, ct).ConfigureAwait(false);
        if (!snapshot.IsAvailable)
        {
            return [];
        }

        var pending = await PendingContainersAsync(tenant, principal, ct).ConfigureAwait(false);
        return snapshot.Containers.Values
            .Where(c => !c.IsSystemUnassigned && (kind is null || c.Container.Kind == kind))
            .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static c => c.Container.Id)
            .Take(page)
            .Select(c => ToTile(c, snapshot, pending))
            .ToList();
    }

    public async ValueTask<ContainerTile?> GetAsync(
        TenantId tenant, ClaimsPrincipal principal, ContainerKind kind, Guid id, CancellationToken ct = default)
    {
        var snapshot = await _snapshots.GetAsync(tenant, principal, ct).ConfigureAwait(false);
        if (!snapshot.IsAvailable
            || !snapshot.Containers.TryGetValue(new ContainerRef(kind, id), out var info)
            || info.IsSystemUnassigned)
        {
            return null;
        }

        var pending = await PendingContainersAsync(tenant, principal, ct).ConfigureAwait(false);
        return ToTile(info, snapshot, pending);
    }

    private static ContainerTile ToTile(ContainerInfo info, ContainerVisibilitySnapshot snapshot, IReadOnlySet<ContainerRef> pending)
    {
        var state = snapshot.AccessState(info.Container);
        if (state == "NONE" && pending.Contains(info.Container))
        {
            state = "PENDING";
        }

        return new ContainerTile(
            info.Container.Kind == ContainerKind.Domain ? "domain" : "dataProduct",
            info.Container.Id,
            info.Name,
            info.Description,
            info.Contact,
            info.Visibility.ToCanonicalName(),
            state);
    }

    private async ValueTask<IReadOnlySet<ContainerRef>> PendingContainersAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct)
    {
        var result = new HashSet<ContainerRef>();
        if (approvals is null || principal.GetUserSid() is not { } user)
        {
            return result;
        }

        try
        {
            var requests = await approvals.GetRequestsByRequesterSidAsync(user, tenant, "PENDING", ct).ConfigureAwait(false);
            foreach (var request in requests)
            {
                if (request.RequestKind == ConsentRequestKind.Catalog
                    && request.TenantId == tenant
                    && request.ContainerKind is { } containerKind
                    && request.ContainerId is { } containerId)
                {
                    result.Add(new ContainerRef(containerKind, containerId));
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The pending marker is informational; without it the tile shows NONE (no access is implied).
        }

        return result;
    }
}
