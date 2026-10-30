using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Issue #404: write seam of the physical non-unique indexes (own partial so the catalog partials do not grow).</summary>
public partial class SqliteGovernanceRepository : IPhysicalIndexRepository
{
    public async Task<bool> ReplacePhysicalIndexesAsync(TableIdentifier table, IReadOnlyList<PhysicalIndexDefinition> indexes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        bool found;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
            try
            {
                found = await CatalogPhysicalIndexStore.ReplaceForTableAsync(
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, table, indexes,
                    () => IncrementTableEpochInternalAsync(conn, table, tx, ct), ct).ConfigureAwait(false);
                if (!found)
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }

                // Queued inside the write scope: the scope disposal applies it under the write gate.
                QueueMetadataInvalidation(table);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(table, ct);
        return found;
    }
}
