using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// SQL Server: <c>sys.columns</c>, <c>sys.objects</c>, <c>sys.schemas</c> and <c>sys.types</c>, schema-qualified and joined by
/// bound names. The type is the base system type (an alias type resolves to what the engine stores); the collation is the
/// per-column <c>collation_name</c>, which is what the comparison uses, not the database default.
/// </summary>
public sealed class SqlServerColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql = """
        SELECT c.name, bt.name, c.max_length, c.precision, c.scale, c.collation_name
        FROM sys.columns AS c
        JOIN sys.objects AS o ON o.object_id = c.object_id AND o.type IN ('U', 'V')
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        JOIN sys.types AS bt ON bt.user_type_id = c.system_type_id
        WHERE s.name = @schema AND o.name = @table
        ORDER BY c.column_id
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
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
            string type = FormatType(
                reader.GetString(1), Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture), Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture));
            yield return new DbColumnMetadata(reader.GetString(0), type, reader.IsDBNull(5) ? null : reader.GetString(5));
        }
    }

    internal static string FormatType(string name, int maxLength, int precision, int scale) => name switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{name}({Length(maxLength)})",
        "nchar" or "nvarchar" => $"{name}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{name}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "time" or "datetimeoffset" => $"{name}({scale.ToString(CultureInfo.InvariantCulture)})",
        "float" => precision == 53 ? "float" : $"float({precision.ToString(CultureInfo.InvariantCulture)})",
        _ => name
    };

    private static string Length(int maxLength) => maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture);
}
