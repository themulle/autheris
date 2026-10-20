namespace Autheris.Application.Governance.Rights;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public interface IFunctionalRightMapper
{
    Task<IReadOnlySet<FunctionalRight>> GetRightsForCallerAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        string realmId,
        CancellationToken ct = default);

    Task<IReadOnlySet<FunctionalRight>> GetRightsForGroupsAndRolesAsync(
        TenantId tenantId,
        string realmId,
        IReadOnlyCollection<string> groupsAndRoles,
        CancellationToken ct = default);

    Task<bool> HasRightAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        string realmId,
        FunctionalRight right,
        CancellationToken ct = default);

    Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default);
    Task<FunctionalRightMapping> ProposeMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        RecertClass recertClass,
        string createdBy,
        CancellationToken ct = default);

    Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight right, string approverId, CancellationToken ct = default);

    Task DeleteMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        string deletedBy,
        CancellationToken ct = default);
}
