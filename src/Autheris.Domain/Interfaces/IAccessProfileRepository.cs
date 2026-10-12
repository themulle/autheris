namespace Autheris.Domain.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IAccessProfileRepository
{
    Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default);
    Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default);
    Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default);
    Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default);
    Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default);
}
