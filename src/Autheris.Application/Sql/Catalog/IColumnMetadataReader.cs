using System.Data.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Sql.Catalog;

/// <summary>
/// One column as the database reports it.
/// </summary>
/// <param name="Name">Column name in the exact catalog spelling.</param>
/// <param name="NativeType">Provider-native type including length, precision and scale, for example <c>nvarchar(64)</c> or <c>numeric(18,2)</c>.</param>
/// <param name="Collation">Column collation, or null when the database has none (SQLite, DuckDB). The value is only compared with constants and is never emitted into SQL.</param>
/// <param name="LengthSemantics">Oracle only: <c>char</c> or <c>byte</c> length semantics (<c>CHAR_USED</c>); part of the version hash.</param>
public readonly record struct DbColumnMetadata(string Name, string NativeType, string? Collation, string? LengthSemantics = null);

/// <summary>
/// Reads column metadata from the system catalog of one dialect. Implementations query fully qualified system catalogs
/// with bound names only (SEC-X1-09), so a synonym or a table in the current schema cannot shadow the catalog.
/// </summary>
public interface IColumnMetadataReader
{
    TargetSqlDialect Dialect { get; }

    IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table, CancellationToken ct);
}
