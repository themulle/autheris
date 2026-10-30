using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Issue #404: write seam of the physical non-unique indexes (own partial so the catalog partials do not grow).</summary>
public partial class SqlServerGovernanceRepository : IPhysicalIndexRepository
{
    public async Task<bool> ReplacePhysicalIndexesAsync(TableIdentifier table, IReadOnlyList<PhysicalIndexDefinition> indexes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        bool found;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                found = await CatalogPhysicalIndexStore.ReplaceForTableAsync(
                    conn, tx, CatalogKeyDefinitionStore.SqlServer, table, indexes,
                    () => IncrementTableEpochInternalAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (!found)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        return found;
    }
}
