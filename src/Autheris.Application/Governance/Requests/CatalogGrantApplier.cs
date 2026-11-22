using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Materializes approved <see cref="ConsentRequestKind.Catalog"/> requests exclusively into
/// <see cref="ICatalogVisibilityGrantRepository"/> (<c>catalog_visibility_grants</c>), never into data consent tables.
/// </summary>
public sealed class CatalogGrantApplier(ICatalogVisibilityGrantRepository catalogGrants) : IGrantEffectApplier
{
    private readonly ICatalogVisibilityGrantRepository _catalogGrants = catalogGrants ?? throw new ArgumentNullException(nameof(catalogGrants));

    public ConsentRequestKind RequestKind => ConsentRequestKind.Catalog;

    public async Task<IReadOnlyList<GrantRef>> ApplyAsync(
        IGovernanceTransaction transaction,
        ApprovedPermission approved,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approved);

        var req = approved.Request;
        if (!req.ContainerKind.HasValue || !req.ContainerId.HasValue || req.ContainerId.Value == Guid.Empty)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Catalog visibility grant requires a containerKind and containerId.", 422);
        }

        if (string.IsNullOrWhiteSpace(req.RequestedGranteeRef))
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "Grant target grantee reference is missing.", 422);
        }

        var grant = new CatalogVisibilityGrant
        {
            Id = Guid.NewGuid(),
            TenantId = req.TenantId,
            ContainerKind = req.ContainerKind.Value,
            ContainerId = req.ContainerId.Value,
            GranteeType = req.RequestedGranteeType,
            GranteeSid = new Sid(req.RequestedGranteeRef),
            ValidFrom = approved.ValidFrom,
            ValidTo = approved.ValidTo < DateTimeOffset.MaxValue ? approved.ValidTo : null,
            GrantedBySid = approved.DecidedBy,
            ApproverCapacity = approved.Capacity,
            RequestId = req.Id,
            CreatedAt = approved.DecidedAt
        };

        var persisted = await _catalogGrants.CreateAsync(grant, transaction?.Transaction, ct).ConfigureAwait(false);
        return
        [
            new GrantRef(
                persisted.Id,
                PermissionGrantKind.Catalog,
                ContainerKind: persisted.ContainerKind,
                ContainerId: persisted.ContainerId,
                ValidFrom: persisted.ValidFrom,
                ValidTo: persisted.ValidTo)
        ];
    }
}
