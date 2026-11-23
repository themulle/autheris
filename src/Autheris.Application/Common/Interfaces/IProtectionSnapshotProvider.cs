using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Common.Interfaces;

/// <summary>Provides cached, epoch-coherent effective protection snapshots for governed tables.</summary>
public interface IProtectionSnapshotProvider
{
    ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// The snapshots of a batch of decisions, keyed by table identifier: cached ones as <see cref="GetAsync"/> returns them, the others
    /// resolved from <paramref name="tables"/> (the metadata the decisions use) with set-based reads. Resolved snapshots are not cached,
    /// so they serve only the batch that asked for them. A table missing from the result is resolved per decision. The default answers
    /// every table with <see cref="GetAsync"/>.
    /// </summary>
    async ValueTask<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>> ResolveManyAsync(
        TenantId tenant, IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var result = new Dictionary<TableIdentifier, EffectiveProtection>(tables.Count);
        foreach (var table in tables)
        {
            result[table.Identifier] = await GetAsync(tenant, table.Identifier, ct).ConfigureAwait(false);
        }

        return result;
    }
}
