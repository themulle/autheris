namespace Autheris.Application.Governance.Rights;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.SingleApproval;
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

    /// <summary>Saves a mapping; the save is the approval (single approval), audited with a before/after hash.</summary>
    Task<FunctionalRightMapping> SaveMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        string recertClass,
        SingleApprovalWriter writer,
        CancellationToken ct = default);

    Task DeleteMappingAsync(
        TenantId tenantId,
        string realmId,
        string groupOrRole,
        FunctionalRight right,
        SingleApprovalWriter writer,
        CancellationToken ct = default);
}
