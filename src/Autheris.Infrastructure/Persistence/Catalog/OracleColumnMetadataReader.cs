using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>
/// Oracle: reads <c>SYS.ALL_TAB_COLUMNS</c>, never the unqualified public synonym, which a table of the same name in
/// <c>CURRENT_SCHEMA</c> would shadow (SEC-X1-09). Owner and table are bound by name. <c>CHAR_USED</c> becomes the length
/// semantics (<c>char</c> or <c>byte</c>); <c>COLLATION</c> is the column collation (null for non-character columns).
/// </summary>
public sealed class OracleColumnMetadataReader : IColumnMetadataReader
{
    private const string Sql = """
        SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED, DATA_PRECISION, DATA_SCALE, COLLATION
        FROM SYS.ALL_TAB_COLUMNS
        WHERE OWNER = :owner AND TABLE_NAME = :tbl
        ORDER BY COLUMN_ID
        """;

    public TargetSqlDialect Dialect => TargetSqlDialect.Oracle;

    public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        OracleBindByName.Enable(command);
        CatalogQuery.Bind(command, "owner", table.Schema);
        CatalogQuery.Bind(command, "tbl", table.Table);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string dataType = reader.GetString(1);
            int? dataLength = Int(reader, 2);
            int? charLength = Int(reader, 3);
            string? charUsed = reader.IsDBNull(4) ? null : reader.GetString(4);
            int? precision = Int(reader, 5);
            int? scale = Int(reader, 6);
            string? semantics = charUsed switch { "C" => "char", "B" => "byte", _ => null };
            int? length = charUsed == "C" ? charLength : dataLength;
            yield return new DbColumnMetadata(
                reader.GetString(0), FormatType(dataType, length, precision, scale), reader.IsDBNull(7) ? null : reader.GetString(7), semantics);
        }
    }

    internal static string FormatType(string dataType, int? length, int? precision, int? scale)
    {
        string name = dataType.ToLowerInvariant();
        return name switch
        {
            "varchar2" or "nvarchar2" or "char" or "nchar" or "raw" when length is not null =>
                $"{name}({length.Value.ToString(CultureInfo.InvariantCulture)})",
            "number" when precision is null && scale is null => "number",
            "number" => scale is null or 0
                ? $"number({(precision ?? 38).ToString(CultureInfo.InvariantCulture)})"   // NUMBER(*,s) has a null precision: 38 is the maximum
                : $"number({(precision ?? 38).ToString(CultureInfo.InvariantCulture)},{scale.Value.ToString(CultureInfo.InvariantCulture)})",
            "float" when precision is not null => $"float({precision.Value.ToString(CultureInfo.InvariantCulture)})",
            _ => name   // DATA_TYPE already carries the fractional seconds precision of TIMESTAMP(n) [WITH TIME ZONE]
        };
    }

    private static int? Int(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
