using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// SQLite: the declared type is free text and a column has no collation in the metadata, so the collation is always null
/// (treated as "not binary", fail safe). The table-valued pragma takes the table and schema names as bound parameters.
/// </summary>
public sealed class SqliteColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql = "SELECT name, type FROM pragma_table_info(@t, @s) ORDER BY cid";

    public TargetSqlDialect Dialect => TargetSqlDialect.Sqlite;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        CatalogQuery.Bind(command, "@t", table.Table);
        CatalogQuery.Bind(command, "@s", table.Schema);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new DbColumnMetadata(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), null);
        }
    }
}
