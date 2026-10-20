namespace Autheris.Domain.Caching.TableCache;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Orchestrates table cache life-cycle, single-flight request coalescing, and quota management.
/// </summary>
public interface ITableCacheManager
{
    ValueTask<TableCacheEntryMetadata?> GetOrLoadAsync(TableIdentifier table, CancellationToken ct = default);

    Task ForceRefreshAsync(TableIdentifier table, CancellationToken ct = default);

    Task EvictExpiredAsync(CancellationToken ct = default);

    TableCacheStats GetStats();
}
