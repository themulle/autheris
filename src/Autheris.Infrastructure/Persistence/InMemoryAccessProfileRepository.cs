namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemoryAccessProfileRepository : IAccessProfileRepository
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string ProfileId), AccessProfile> _profiles = new();

    public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var now = DateTimeOffset.UtcNow;
        var list = _profiles.Values
            .Where(p => p.TenantId == tenantId &&
                        p.IsActive(now) &&
                        p.AssignedSubjects.Any(s => string.Equals(s, subject, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return Task.FromResult<IReadOnlyList<AccessProfile>>(list);
    }

    public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var list = _profiles.Values
            .Where(p => p.TenantId == tenantId)
            .OrderBy(p => p.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<AccessProfile>>(list);
    }

    public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        _profiles.TryGetValue((tenantId, profileId), out var profile);
        return Task.FromResult(profile);
    }

    public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ProfileId);

        _profiles[(profile.TenantId, profile.ProfileId)] = profile;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var removed = _profiles.TryRemove((tenantId, profileId), out _);
        return Task.FromResult(removed);
    }
}
