namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;

public partial class SqliteGovernanceRepository : IFunctionalRightRepository
{
    private RightsAndPoliciesStore? _rightsAndPoliciesStore;

    private RightsAndPoliciesStore RightsAndPoliciesInstance => _rightsAndPoliciesStore ??= new RightsAndPoliciesStore(RightsAndPoliciesDialect.Sqlite, async (write, ct) =>
    {
        var scope = write
            ? await WriteScopeAsync(ct).ConfigureAwait(false)
            : await ReadScopeAsync(ct).ConfigureAwait(false);
        return new RightsAndPoliciesScope(scope.Connection, scope.DisposeAsync);
    });

    // ---------------------------------------------------- FUNCTIONAL RIGHTS
    public Task<IReadOnlyList<FunctionalRightMapping>> GetMappingsAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.GetMappingsAsync(tenantId, realmId, ct);

    public Task<IReadOnlyList<FunctionalRightMapping>> GetApprovedMappingsForGroupsAndRolesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.GetApprovedMappingsForGroupsAndRolesAsync(tenantId, realmId, groupsAndRoles, ct);

    public Task<FunctionalRightMapping?> GetMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.GetMappingAsync(tenantId, realmId, groupOrRole, functionalRight, ct);

    public Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.CreateMappingAsync(mapping, ct);

    public Task ApproveMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, string approvedBy, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.ApproveMappingAsync(tenantId, realmId, groupOrRole, functionalRight, approvedBy, ct);

    public Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) =>
        RightsAndPoliciesInstance.DeleteMappingAsync(tenantId, realmId, groupOrRole, functionalRight, ct);
}
