using System.Data.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Sql.Catalog;

/// <summary>
/// One key column of a primary key or unique index as the database reports it (I403 D-1).
/// </summary>
/// <param name="IndexName">Physical index or constraint name; data only, never emitted into SQL, logs, audit or responses (I403-SC-02, SC-09).</param>
/// <param name="IsPrimaryKey">True when the index backs the primary key.</param>
/// <param name="IsClustered">True when the index is the clustered index (SQL Server <c>type = 1</c>, PostgreSQL <c>indisclustered</c>).</param>
/// <param name="KeyOrdinal">One-based position of the column within the key.</param>
/// <param name="ColumnName">Column name in the exact catalog spelling.</param>
/// <param name="IsDescending">True when the key column is stored in descending order.</param>
/// <param name="IsUnique">True when the index is unique (a primary key is unique). Issue #404: non-unique indexes are read as well; the default is unique so a caller that does not say never produces a non-unique index.</param>
public readonly record struct DbKeyIndexColumn(string IndexName, bool IsPrimaryKey, bool IsClustered, int KeyOrdinal, string ColumnName, bool IsDescending, bool IsUnique = true);

/// <summary>
/// Reads the key columns (with direction) of the primary key, the unique indexes and (issue #404) the non-unique indexes from the system catalog of one dialect.
/// Rows are ordered primary key, unique, non-unique, then index and key ordinal. One row per key column of each unfiltered, enabled, non-expression index; included columns are excluded. Implementations
/// query fully qualified system catalogs with bound names only (SEC-X1-09, I403-SC-02).
/// </summary>
public interface IKeyIndexMetadataReader
{
    TargetSqlDialect Dialect { get; }

    IAsyncEnumerable<DbKeyIndexColumn> ReadAsync(DbConnection connection, TableIdentity table, CancellationToken ct);
}
