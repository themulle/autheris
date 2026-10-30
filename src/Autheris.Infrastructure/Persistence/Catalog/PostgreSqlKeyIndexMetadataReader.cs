using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// PostgreSQL: every catalog relation and function is <c>pg_catalog</c>-qualified (SEC-X1-09). Only valid, ready, unfiltered,
/// non-expression unique btree indexes (other access methods have no order); key columns only (<c>ord &lt;= indnkeyatts</c>).
/// <c>indoption</c> is a zero-based int2vector and bit 1 is DESC; the NULLS FIRST bit is ignored.
/// </summary>
public sealed class PostgreSqlKeyIndexMetadataReader : IKeyIndexMetadataReader
{
    internal const string Sql = """
        SELECT ic.relname::text, i.indisprimary, i.indisclustered, u.ord::int, a.attname::text,
               ((i.indoption[(u.ord - 1)::int] & 1) = 1)
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class k ON k.oid = i.indrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = k.relnamespace
        JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_catalog.pg_am am ON am.oid = ic.relam
        CROSS JOIN LATERAL pg_catalog.unnest(i.indkey) WITH ORDINALITY AS u(attnum, ord)
        JOIN pg_catalog.pg_attribute a ON a.attrelid = k.oid AND a.attnum = u.attnum
        WHERE n.nspname = @schema AND k.relname = @table AND am.amname = 'btree'
          AND i.indisunique AND i.indisvalid AND i.indisready AND i.indpred IS NULL AND i.indexprs IS NULL
          AND u.ord <= i.indnkeyatts
        ORDER BY i.indexrelid, u.ord
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.PostgreSql;

    public async IAsyncEnumerable<DbKeyIndexColumn> ReadAsync(DbConnection connection, TableIdentity table,
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
            yield return new DbKeyIndexColumn(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetString(4),
                reader.GetBoolean(5));
        }
    }
}
