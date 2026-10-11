using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// DuckDB: reads the built-in <c>system.main.duckdb_columns()</c> function. DuckDB does not expose a column collation in its
/// metadata (a <c>VARCHAR COLLATE NOCASE</c> column reports plain <c>VARCHAR</c>), so the collation is always null, which the
/// compiler treats as "not binary" (fail safe). The type is lower-cased.
/// </summary>
public sealed class DuckDbColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql =
        "SELECT column_name, data_type FROM system.main.duckdb_columns() " +
        "WHERE database_name = current_database() AND schema_name = $s AND table_name = $t ORDER BY column_index";

    public TargetSqlDialect Dialect => TargetSqlDialect.DuckDb;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        CatalogQuery.Bind(command, "s", table.Schema);
        CatalogQuery.Bind(command, "t", table.Table);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new DbColumnMetadata(reader.GetString(0), reader.GetString(1).Trim().ToLowerInvariant(), null);
        }
    }
}
