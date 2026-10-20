namespace Autheris.Domain.Caching.TableCache;

/// <summary>
/// Declares the physical storage medium used for the cached table.
/// </summary>
public enum TableCacheStorageTier
{
    /// <summary>Volatile in-memory DuckDB session (transient, destroyed on process exit).</summary>
    Memory = 1,

    /// <summary>Persistent local DuckDB database file (.duckdb) encrypted at rest.</summary>
    DuckDbDisk = 2,

    /// <summary>Partitioned Apache Parquet columnar files encrypted at rest.</summary>
    ParquetDisk = 3
}
