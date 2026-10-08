namespace Autheris.Application.VirtualFilters;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>All virtual filters and bindings with the generation they belong to.</summary>
public sealed record VirtualFilterSnapshot(long Generation, IReadOnlyList<VirtualFilter> Filters, IReadOnlyList<FilterBinding> Bindings)
{
    public static readonly VirtualFilterSnapshot Empty = new(0, [], []);
}

/// <summary>
/// Storage of virtual filters and bindings. Every successful write increments the global generation in the same
/// transaction, so enforcement can reload exactly when something changed. Rules (validation, locks, references)
/// live in <see cref="VirtualFilterAdministrationService"/>, not here.
/// </summary>
public interface IVirtualFilterRepository
{
    Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default);

    Task<long> GetGenerationAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces the filter with the same tenant and name.</summary>
    Task SaveFilterAsync(VirtualFilter filter, CancellationToken ct = default);

    Task<bool> DeleteFilterAsync(TenantId tenantId, string name, CancellationToken ct = default);

    /// <summary>Inserts or replaces the binding with the same id.</summary>
    Task SaveBindingAsync(FilterBinding binding, CancellationToken ct = default);

    Task<bool> DeleteBindingAsync(TenantId tenantId, Guid id, CancellationToken ct = default);
}
