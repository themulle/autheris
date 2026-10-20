namespace Autheris.Application.Caching.TableCache;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;

/// <summary>
/// Thread-safe in-memory table cache catalog backed by immutable FrozenDictionary snapshots.
/// Provides zero-allocation, lock-free O(1) reads for query routing and inspection.
/// Conforms to WP-TC-01.
/// </summary>
public sealed class TableCacheCatalog : ITableCacheCatalog
{
    private readonly object _syncLock = new();
    private readonly Dictionary<TableIdentifier, TableCacheEntryMetadata> _entries = new();
    private volatile FrozenDictionary<TableIdentifier, TableCacheEntryMetadata> _snapshot =
        FrozenDictionary<TableIdentifier, TableCacheEntryMetadata>.Empty;

    public bool TryGet(TableIdentifier table, out TableCacheEntryMetadata? metadata)
    {
        return _snapshot.TryGetValue(table, out metadata);
    }

    public void Upsert(TableCacheEntryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_syncLock)
        {
            _entries[metadata.Table] = metadata;
            _snapshot = _entries.ToFrozenDictionary();
        }
    }

    public bool Remove(TableIdentifier table)
    {
        lock (_syncLock)
        {
            if (_entries.Remove(table))
            {
                _snapshot = _entries.ToFrozenDictionary();
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<TableCacheEntryMetadata> GetAll()
    {
        return _snapshot.Values as IReadOnlyList<TableCacheEntryMetadata> ?? [.. _snapshot.Values];
    }

    public FrozenDictionary<TableIdentifier, TableCacheEntryMetadata> GetSnapshot()
    {
        return _snapshot;
    }
}
