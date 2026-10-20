namespace Autheris.Domain.Caching.TableCache;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Handles manual or event-driven eviction and invalidation of cached tables and data sources.
/// </summary>
public interface ITableCacheInvalidator
{
    Task InvalidateAsync(TableIdentifier table, string reason, CancellationToken ct = default);

    Task InvalidateDataSourceAsync(string dataSource, string reason, CancellationToken ct = default);
}
