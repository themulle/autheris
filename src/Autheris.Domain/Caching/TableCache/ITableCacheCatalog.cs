namespace Autheris.Domain.Caching.TableCache;

using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Fast thread-safe catalog for registered cached tables with FrozenDictionary snapshots.
/// </summary>
public interface ITableCacheCatalog
{
    bool TryGet(TableIdentifier table, out TableCacheEntryMetadata? metadata);

    void Upsert(TableCacheEntryMetadata metadata);

    bool Remove(TableIdentifier table);

    IReadOnlyList<TableCacheEntryMetadata> GetAll();

    FrozenDictionary<TableIdentifier, TableCacheEntryMetadata> GetSnapshot();
}
