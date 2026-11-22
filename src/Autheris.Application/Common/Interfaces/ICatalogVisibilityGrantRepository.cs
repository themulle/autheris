using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>
/// Tenant- and container-scoped repository for catalog visibility grants stored in <c>catalog_visibility_grants</c>.
/// Catalog grants expose metadata only and never authorize data access.
/// </summary>
public interface ICatalogVisibilityGrantRepository
{
    Task<CatalogVisibilityGrant> CreateAsync(
        CatalogVisibilityGrant grant,
        DbTransaction? transaction = null,
        CancellationToken ct = default);

    Task<CatalogVisibilityGrant?> GetByIdAsync(
        TenantId tenantId,
        Guid grantId,
        CancellationToken ct = default);

    Task<bool> RevokeAsync(
        TenantId tenantId,
        Guid grantId,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        DbTransaction? transaction = null,
        CancellationToken ct = default);

    Task<int> RevokeAllForSubjectAsync(
        TenantId tenantId,
        IReadOnlyCollection<Sid> subjects,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        DbTransaction? transaction = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<CatalogVisibilityGrant>> ListActiveForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<Sid> subjects,
        DateTimeOffset atTime,
        CancellationToken ct = default);

    Task<IReadOnlyList<CatalogVisibilityGrant>> ListByContainerAsync(
        TenantId tenantId,
        ContainerKind containerKind,
        Guid containerId,
        bool includeInactive = false,
        CancellationToken ct = default);
}
