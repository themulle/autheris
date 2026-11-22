namespace Autheris.Application.Policy.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// AR-01: Multi-node cluster-consistent access profile cache abstraction with epoch-keying.
/// </summary>
public interface IAccessProfileCache
{
    /// <summary>
    /// Returns the subject's profiles. Throws <see cref="Autheris.Application.Policy.Exceptions.AccessProfileSourceUnavailableException"/>
    /// when neither an epoch-validated cache entry nor the DB is available (caller: deny).
    /// </summary>
    ValueTask<IReadOnlyList<AccessProfile>> GetProfilesAsync(TenantId tenantId, string subject, CancellationToken ct = default);

    /// <summary>
    /// the profiles that are effective for a caller found through ALL its user ids (SIDs, aliases), groups and roles in one repository call
    /// (<see cref="Autheris.Domain.Interfaces.IAccessProfileRepository.GetProfilesForSubjectsAsync"/>), most restrictive first. The cache key is the tenant, the
    /// epoch and a hash of the normalised subject set, so callers with the same identities share an entry and a tenant never sees another tenant's entry.
    /// Throws <see cref="Autheris.Application.Policy.Exceptions.AccessProfileSourceUnavailableException"/> when the source is unavailable (caller: deny).
    /// </summary>
    ValueTask<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> users,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> roles,
        CancellationToken ct = default);

    /// <summary>
    /// Bumps the tenant epoch in the cluster store and publishes best-effort invalidation; throws on store failure.
    /// </summary>
    Task InvalidateTenantAsync(TenantId tenantId, CancellationToken ct = default);
}
