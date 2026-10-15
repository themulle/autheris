using System.Data.Common;
using Autheris.Infrastructure.Persistence.Catalog;
using static Autheris.Infrastructure.Persistence.Migrations.Shape.SchemaCatalogReaderSupport;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>SQLite catalog reader (<c>sqlite_master</c>, <c>pragma_table_info</c>, <c>pragma_index_list</c>, <c>pragma_index_info</c>).</summary>
internal sealed class SqliteSchemaCatalogReader : IGovernanceSchemaCatalogReader
{
    public async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, CancellationToken ct)
    {
        var rows = await RowsAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND lower(name) <> @migrations ORDER BY name",
            1,
            ct,
            ("@migrations", MigrationTable)).ConfigureAwait(false);
        return rows.Select(r => Str(r[0])).ToArray();
    }

    public async Task<GovernanceSchemaShape> ReadAsync(DbConnection connection, IReadOnlyCollection<string> tableNames, CancellationToken ct)
    {
        var tables = new List<SchemaTable>();
        foreach (var requested in tableNames)
        {
            var exists = await RowsAsync(
                connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @n COLLATE NOCASE", 1, ct, ("@n", requested)).ConfigureAwait(false);
            if (exists.Count == 0)
            {
                continue;
            }

            var name = Str(exists[0][0]);
            var columnRows = await RowsAsync(connection, "SELECT name, type, pk FROM pragma_table_info(@n)", 3, ct, ("@n", name)).ConfigureAwait(false);
            var columns = columnRows
                .Select(r => new SchemaColumn(Str(r[0]), Str(r[1]).ToUpperInvariant(), Family(Str(r[1]))))
                .ToArray();
            var primaryKey = columnRows
                .Where(r => Convert.ToInt64(r[2], System.Globalization.CultureInfo.InvariantCulture) > 0)
                .OrderBy(r => Convert.ToInt64(r[2], System.Globalization.CultureInfo.InvariantCulture))
                .Select(r => Str(r[0]))
                .ToArray();

            var indexes = new List<SchemaIndex>();
            var indexRows = await RowsAsync(
                connection, "SELECT name, \"unique\" FROM pragma_index_list(@n) WHERE origin = 'c' ORDER BY name", 2, ct, ("@n", name)).ConfigureAwait(false);
            foreach (var index in indexRows)
            {
                var indexName = Str(index[0]);
                var columnsOfIndex = await RowsAsync(
                    connection, "SELECT name FROM pragma_index_info(@i) ORDER BY seqno", 1, ct, ("@i", indexName)).ConfigureAwait(false);
                indexes.Add(new SchemaIndex(indexName, Bool(index[1]), columnsOfIndex.Select(c => c[0] is null ? "(expression)" : Str(c[0])).ToArray()));
            }

            tables.Add(new SchemaTable(name, columns, primaryKey, indexes));
        }

        return new GovernanceSchemaShape("sqlite", tables);
    }

    internal static string Family(string declaredType) => SqliteTypeAffinity.Of(declaredType) switch
    {
        SqliteAffinity.IntegerAffinity => SchemaTypeFamily.Integer,
        SqliteAffinity.TextAffinity => SchemaTypeFamily.String,
        SqliteAffinity.BlobAffinity => SchemaTypeFamily.Binary,
        SqliteAffinity.RealAffinity => SchemaTypeFamily.Float,
        _ => SchemaTypeFamily.Decimal
    };
}
