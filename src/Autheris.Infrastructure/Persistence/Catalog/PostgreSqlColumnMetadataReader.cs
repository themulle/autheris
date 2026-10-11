using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// PostgreSQL: every catalog relation and function is <c>pg_catalog</c>-qualified, so a relation of the same name earlier on
/// <c>search_path</c> cannot shadow it (SEC-X1-09). The type comes from <c>pg_catalog.format_type</c> (<c>numeric(18,3)</c>,
/// <c>character varying(64)</c>); a column of a non-collatable type has a null collation.
/// </summary>
public sealed class PostgreSqlColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql = """
        SELECT a.attname::text,
               pg_catalog.format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attcollation = 0 THEN NULL ELSE co.collname::text END
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class k ON k.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = k.relnamespace
        LEFT JOIN pg_catalog.pg_collation co ON co.oid = a.attcollation
        WHERE n.nspname = @schema AND k.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.PostgreSql;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        CatalogQuery.Bind(command, "schema", table.Schema);
        CatalogQuery.Bind(command, "table", table.Table);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new DbColumnMetadata(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }
}
