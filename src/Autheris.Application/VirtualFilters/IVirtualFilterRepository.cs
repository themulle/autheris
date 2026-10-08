namespace Autheris.Application.VirtualFilters;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>All virtual filters and access profiles with the generation they belong to.</summary>
public sealed record VirtualFilterSnapshot(long Generation, IReadOnlyList<VirtualFilter> Filters, IReadOnlyList<AccessProfile> Profiles)
{
    public static readonly VirtualFilterSnapshot Empty = new(0, [], []);
}

/// <summary>A set of writes applied in one transaction: saves (insert or replace by tenant and name), then deletes.</summary>
public sealed record VirtualFilterChangeSet
{
    public IReadOnlyList<VirtualFilter> SaveFilters { get; init; } = [];
    public IReadOnlyList<AccessProfile> SaveProfiles { get; init; } = [];
    public IReadOnlyList<(TenantId Tenant, string Name)> DeleteProfiles { get; init; } = [];
    public IReadOnlyList<(TenantId Tenant, string Name)> DeleteFilters { get; init; } = [];

    public bool IsEmpty => SaveFilters.Count + SaveProfiles.Count + DeleteProfiles.Count + DeleteFilters.Count == 0;
}

/// <summary>
/// Storage of virtual filters and access profiles. A change set is applied in one transaction (all or nothing) and
/// increments the global generation once, so enforcement reloads exactly when something changed. Rules (validation,
/// locks, references) live in <see cref="VirtualFilterAdministrationService"/>, not here.
/// </summary>
public interface IVirtualFilterRepository
{
    Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default);

    Task<long> GetGenerationAsync(CancellationToken ct = default);

    Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default);
}
