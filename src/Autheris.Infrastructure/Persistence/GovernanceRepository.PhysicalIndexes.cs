using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Write seam of the physical non-unique indexes (own partial so the catalog partial does not grow).</summary>
public partial class GovernanceRepository : IPhysicalIndexRepository
{
    public async Task<bool> ReplacePhysicalIndexesAsync(TableIdentifier table, IReadOnlyList<PhysicalIndexDefinition> indexes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        bool found;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                found = await CatalogPhysicalIndexStore.ReplaceForTableAsync(
                    conn, tx, KeyDialect, table, indexes,
                    () => IncrementTableEpochInternalAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (!found)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }

                var newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 1, 0, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        return found;
    }
}
