using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// Databricks: reads the Unity Catalog <c>information_schema.columns</c> of the session catalog through the REST facade (WP-X0j).
/// <c>full_data_type</c> carries length, precision and scale; <c>collation_name</c> is the column collation (null when none).
/// Schema and table are bound parameters; the catalog is the connection's configured catalog (<c>current_catalog()</c>), so a
/// same-named schema in another catalog cannot shadow it. Unity Catalog identifiers are case-insensitive and stored lower-case.
/// </summary>
public sealed class DatabricksColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql = """
        SELECT column_name, full_data_type, collation_name
        FROM information_schema.columns
        WHERE table_catalog = current_catalog() AND lower(table_schema) = lower(:sch) AND lower(table_name) = lower(:tbl)
        ORDER BY ordinal_position
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.Databricks;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        CatalogQuery.Bind(command, "sch", table.Schema);
        CatalogQuery.Bind(command, "tbl", table.Table);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new DbColumnMetadata(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }
}
