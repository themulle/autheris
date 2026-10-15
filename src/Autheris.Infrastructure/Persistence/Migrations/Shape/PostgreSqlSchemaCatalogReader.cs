using System.Data.Common;
using static Autheris.Infrastructure.Persistence.Migrations.Shape.SchemaCatalogReaderSupport;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>
/// PostgreSQL catalog reader on <c>pg_catalog</c> (visible to every login, unlike <c>information_schema</c>). Tables resolve the way the
/// runtime resolves unqualified names, through the <c>search_path</c> (<c>to_regclass</c>); a table of the same name in another schema
/// neither satisfies nor hides the check (SEC-94-08d).
/// </summary>
internal sealed class PostgreSqlSchemaCatalogReader : IGovernanceSchemaCatalogReader
{
    // quote_ident keeps the catalog spelling exact; every name is a bound parameter.
    private const string Resolve = "to_regclass(quote_ident(@n))";

    public async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, CancellationToken ct, DbTransaction? transaction = null)
    {
        var session = new CatalogSession(connection, transaction);
        var rows = await RowsAsync(
            session,
            "SELECT c.relname::text FROM pg_class c WHERE c.relkind IN ('r', 'p') " +
            "AND c.relnamespace = (SELECT n.oid FROM pg_namespace n WHERE n.nspname = current_schema()) AND lower(c.relname::text) <> @migrations ORDER BY 1",
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
                $"SELECT c.relname::text FROM pg_class c WHERE c.oid = {Resolve} AND c.relkind IN ('r', 'p')",
                1,
                ct,
                ("@n", requested)).ConfigureAwait(false);
            if (exists.Count == 0)
            {
                continue;
            }

            var name = Str(exists[0][0]);
            var columnRows = await RowsAsync(
                session,
                $"SELECT a.attname::text, t.typname::text, format_type(a.atttypid, a.atttypmod) FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid " +
                $"WHERE a.attrelid = {Resolve} AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum",
                3,
                ct,
                ("@n", name)).ConfigureAwait(false);
            var columns = columnRows.Select(r => new SchemaColumn(Str(r[0]), Str(r[2]).ToUpperInvariant(), Family(Str(r[1])))).ToArray();

            var keyRows = await RowsAsync(
                session,
                $"SELECT a.attname::text FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " +
                $"JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum WHERE i.indrelid = {Resolve} AND i.indisprimary ORDER BY k.ord",
                1,
                ct,
                ("@n", name)).ConfigureAwait(false);

            var indexRows = await RowsAsync(
                session,
                $"SELECT ic.relname::text, i.indisunique, COALESCE(a.attname::text, '(expression)'), k.ord FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid " +
                $"CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum " +
                $"WHERE i.indrelid = {Resolve} AND NOT i.indisprimary AND k.ord <= i.indnkeyatts " +
                $"AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.contype IN ('p', 'u', 'x')) ORDER BY ic.relname, k.ord",
                4,
                ct,
                ("@n", name)).ConfigureAwait(false);
            var indexes = indexRows
                .GroupBy(r => Str(r[0]), StringComparer.Ordinal)
                .Select(g => new SchemaIndex(g.Key, Bool(g.First()[1]), g.Select(r => Str(r[2])).ToArray()))
                .ToArray();

            tables.Add(new SchemaTable(name, columns, keyRows.Select(r => Str(r[0])).ToArray(), indexes));
        }

        return new GovernanceSchemaShape("postgresql", tables);
    }

    internal static string Family(string typname) => typname switch
    {
        "text" or "varchar" or "bpchar" or "name" or "citext" or "uuid" => SchemaTypeFamily.String,
        "int2" or "int4" or "int8" => SchemaTypeFamily.Integer,
        "numeric" => SchemaTypeFamily.Decimal,
        "float4" or "float8" => SchemaTypeFamily.Float,
        "bool" => SchemaTypeFamily.Boolean,
        "timestamp" or "timestamptz" or "date" or "time" or "timetz" => SchemaTypeFamily.DateTime,
        "bytea" => SchemaTypeFamily.Binary,
        "json" or "jsonb" => SchemaTypeFamily.Json,
        _ => SchemaTypeFamily.Other
    };
}
