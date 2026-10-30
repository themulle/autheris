using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// SQL Server: <c>sys.indexes</c>, <c>sys.index_columns</c>, <c>sys.columns</c>, <c>sys.objects</c> and <c>sys.schemas</c>,
/// schema-qualified and joined by bound names. Only enabled, unfiltered primary key and unique indexes; included columns
/// (<c>key_ordinal = 0</c>) are excluded. Needs <c>VIEW DEFINITION</c> on the table, otherwise the result is empty.
/// </summary>
public sealed class SqlServerKeyIndexMetadataReader : IKeyIndexMetadataReader
{
    internal const string Sql = """
        SELECT i.name, i.is_primary_key, CASE WHEN i.type = 1 THEN 1 ELSE 0 END, ic.key_ordinal, c.name, ic.is_descending_key
        FROM sys.indexes AS i
        JOIN sys.objects AS o ON o.object_id = i.object_id AND o.type = 'U'
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0 AND ic.is_included_column = 0
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE s.name = @schema AND o.name = @table
          AND (i.is_primary_key = 1 OR i.is_unique = 1) AND i.has_filter = 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0
        ORDER BY i.index_id, ic.key_ordinal
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;

    public async IAsyncEnumerable<DbKeyIndexColumn> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        CatalogQuery.Bind(command, "@schema", table.Schema);
        CatalogQuery.Bind(command, "@table", table.Table);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new DbKeyIndexColumn(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1,
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetString(4),
                Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture));
        }
    }
}
