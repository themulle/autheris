using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>Provides cached, epoch-coherent effective protection snapshots for governed tables.</summary>
public interface IProtectionSnapshotProvider
{
    ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Loads the snapshots of <paramref name="tables"/> that are not cached yet in a few set-based reads, so the following
    /// <see cref="GetAsync"/> calls of a batch of decisions are cache hits. It never changes what <see cref="GetAsync"/> returns.
    /// The default does nothing.
    /// </summary>
    ValueTask PrefetchAsync(TenantId tenant, IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default) => ValueTask.CompletedTask;
}
