using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>
/// Revokes a single catalog visibility grant and records its audit entry in the same transaction: the entry is written only
/// when the revoke applied, and a failed audit write rolls the revoke back (fail closed both ways).
/// </summary>
public interface ICatalogGrantRevocationWriter
{
    /// <summary>Returns <see langword="false"/> when no active grant was revoked; nothing is then audited.</summary>
    Task<bool> RevokeWithAuditAsync(
        TenantId tenantId,
        Guid grantId,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        AuditLogEntry audit,
        CancellationToken ct = default);
}

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

    /// <summary>
    /// Lists non-revoked, non-expired catalog grants for <paramref name="subjects"/> including scheduled future grants
    /// (<c>valid_from &gt; atTime</c>) so deprovisioning revokes them before they take effect.
    /// </summary>
    Task<IReadOnlyList<CatalogVisibilityGrant>> ListForDeprovisioningAsync(
        TenantId tenantId,
        IReadOnlyCollection<Sid> subjects,
        DateTimeOffset atTime,
        CancellationToken ct = default) =>
        ListActiveForSubjectsAsync(tenantId, subjects, atTime, ct);

    Task<IReadOnlyList<CatalogVisibilityGrant>> ListByContainerAsync(
        TenantId tenantId,
        ContainerKind containerKind,
        Guid containerId,
        bool includeInactive = false,
        CancellationToken ct = default);
}
