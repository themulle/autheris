namespace Autheris.Domain.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;

public interface IAccessPolicyRepository
{
    Task<IReadOnlyList<AccessPolicy>> GetPoliciesAsync(TenantId tenantId, CancellationToken ct = default);
    Task<AccessPolicy?> GetPolicyByIdAsync(TenantId tenantId, string policyId, CancellationToken ct = default);
    Task CreatePolicyAsync(AccessPolicy policy, CancellationToken ct = default);
    Task UpdatePolicyAsync(AccessPolicy policy, CancellationToken ct = default);
    Task DeletePolicyAsync(TenantId tenantId, string policyId, CancellationToken ct = default);
    Task<IReadOnlyList<AccessPolicy>> GetActivePoliciesForGranteesAsync(TenantId tenantId, string realmId, IReadOnlyCollection<string> groupIds, IReadOnlyCollection<string> roles, CancellationToken ct = default);
}
