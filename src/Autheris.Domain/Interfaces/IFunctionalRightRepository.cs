namespace Autheris.Domain.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public interface IFunctionalRightRepository
{
    Task<IReadOnlyList<FunctionalRightMapping>> GetMappingsAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
    Task<IReadOnlyList<FunctionalRightMapping>> GetApprovedMappingsForGroupsAndRolesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default);
    Task<FunctionalRightMapping?> GetMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default);
    Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default);
    Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, string approvedBy, CancellationToken ct = default);
    Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default);
}
