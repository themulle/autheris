using System.Data.Common;
using static Autheris.Infrastructure.Persistence.Migrations.Shape.SchemaCatalogReaderSupport;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>SQL Server catalog reader on <c>sys.tables</c>, <c>sys.columns</c> and <c>sys.indexes</c>; the schema is <c>dbo</c>. Names are bound parameters.</summary>
internal sealed class SqlServerSchemaCatalogReader : IGovernanceSchemaCatalogReader
{
    public async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, CancellationToken ct, DbTransaction? transaction = null)
    {
        var session = new CatalogSession(connection, transaction);
        var rows = await RowsAsync(
            session,
            "SELECT t.name FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(N'dbo') AND LOWER(t.name) <> @migrations ORDER BY t.name",
            1,
            ct,
            ("@migrations", MigrationTable)).ConfigureAwait(false);
        return rows.Select(r => Str(r[0])).ToArray();
    }

    public async Task<GovernanceSchemaShape> ReadAsync(DbConnection connection, IReadOnlyCollection<string> tableNames, CancellationToken ct, DbTransaction? transaction = null)
    {
        var session = new CatalogSession(connection, transaction);
        var tables = new List<SchemaTable>();
        foreach (var requested in tableNames)
        {
            var exists = await RowsAsync(
                session,
                "SELECT t.name, t.object_id FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name = @n",
                2,
                ct,
                ("@n", requested)).ConfigureAwait(false);
            if (exists.Count == 0)
            {
                continue;
            }

            var name = Str(exists[0][0]);
            var objectId = Convert.ToInt32(exists[0][1], System.Globalization.CultureInfo.InvariantCulture);

            var columnRows = await RowsAsync(
                session,
                "SELECT c.name, TYPE_NAME(c.system_type_id) FROM sys.columns c WHERE c.object_id = @o ORDER BY c.column_id",
                2,
                ct,
                ("@o", objectId)).ConfigureAwait(false);
            var columns = columnRows.Select(r => new SchemaColumn(Str(r[0]), Str(r[1]).ToUpperInvariant(), Family(Str(r[1])))).ToArray();

            var keyRows = await RowsAsync(
                session,
                "SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE i.object_id = @o AND i.is_primary_key = 1 AND ic.is_included_column = 0 AND ic.key_ordinal > 0 ORDER BY ic.key_ordinal",
                1,
                ct,
                ("@o", objectId)).ConfigureAwait(false);

            var indexRows = await RowsAsync(
                session,
                "SELECT i.name, i.is_unique, c.name, ic.key_ordinal FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE i.object_id = @o AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type > 0 AND i.name IS NOT NULL " +
                "AND ic.is_included_column = 0 AND ic.key_ordinal > 0 ORDER BY i.name, ic.key_ordinal",
                4,
                ct,
                ("@o", objectId)).ConfigureAwait(false);
            var indexes = indexRows
                .GroupBy(r => Str(r[0]), StringComparer.Ordinal)
                .Select(g => new SchemaIndex(g.Key, Bool(g.First()[1]), g.Select(r => Str(r[2])).ToArray()))
                .ToArray();

            tables.Add(new SchemaTable(name, columns, keyRows.Select(r => Str(r[0])).ToArray(), indexes));
        }

        return new GovernanceSchemaShape("sqlserver", tables);
    }

    internal static string Family(string typeName) => typeName.ToLowerInvariant() switch
    {
        "nvarchar" or "varchar" or "char" or "nchar" or "ntext" or "text" or "uniqueidentifier" or "sysname" or "xml" => SchemaTypeFamily.String,
        "tinyint" or "smallint" or "int" or "bigint" => SchemaTypeFamily.Integer,
        "decimal" or "numeric" or "money" or "smallmoney" => SchemaTypeFamily.Decimal,
        "float" or "real" => SchemaTypeFamily.Float,
        "bit" => SchemaTypeFamily.Boolean,
        "datetime" or "datetime2" or "datetimeoffset" or "date" or "time" or "smalldatetime" => SchemaTypeFamily.DateTime,
        "varbinary" or "binary" or "image" or "timestamp" or "rowversion" => SchemaTypeFamily.Binary,
        _ => SchemaTypeFamily.Other
    };
}
