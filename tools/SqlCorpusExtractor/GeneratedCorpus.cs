namespace SqlCorpusExtractor;

/// <summary>
/// Deterministic statements over the gate fixture schema (plan 9.2): the extracted test strings mostly reference tables that
/// the fixture does not have, so they stop at the database. These templates are executable on every dialect and exercise joins,
/// set operations, subqueries, window functions, masked columns, cross-tenant writes and policy-column assignments.
/// The table list mirrors <c>GateModel.Tables</c> in the gate project; a statement over a missing table or column fails in both engines and is classified <c>both-rejected</c>.
/// </summary>
internal static class GeneratedCorpus
{
    // kind: i = integer, t = text, d = decimal, s = timestamp; a trailing * marks a masked column.
    private static readonly (string Table, (string Column, char Kind, bool Masked)[] Columns)[] Tables =
    [
        ("orders", [("id", 'i', false), ("tenant_id", 't', false), ("customer_id", 'i', false), ("amount", 'd', false), ("status", 't', false),
            ("region", 't', false), ("dept", 't', false), ("created_at", 's', false), ("email", 't', true), ("note", 't', false)]),
        ("customers", [("id", 'i', false), ("tenant_id", 't', false), ("name", 't', false), ("email", 't', true), ("region", 't', false)]),
        ("entitlements", [("id", 'i', false), ("tenant_id", 't', false), ("order_id", 'i', false), ("note", 't', false)]),
        ("employees", [("id", 'i', false), ("tenant_id", 't', false), ("name", 't', false), ("dept", 't', false), ("salary", 'd', false)]),
        ("users", [("id", 'i', false), ("tenant_id", 't', false), ("name", 't', false), ("status", 't', false), ("email", 't', true)])
    ];

    /// <summary>Templates that the governed path must reject (their <c>expected</c> metadata is <c>reject</c>).</summary>
    public static readonly HashSet<string> ExpectedRejected =
    [
        "masked-predicate", "masked-sort", "insert-foreign", "insert-no-tenant", "delete-unfiltered", "delete-tautology",
        "update-tenant", "update-masked", "update-region-policy-column", "system-column"
    ];

    public static IEnumerable<(string Template, string Sql)> Queries()
    {
        foreach (var (table, columns) in Tables)
        {
            yield return ("star", $"SELECT * FROM {table}");
            yield return ("count", $"SELECT count(*) FROM {table}");
            yield return ("top", $"SELECT id FROM {table} ORDER BY id DESC LIMIT 3");
            yield return ("alias", $"SELECT t.id FROM {table} t WHERE t.id > 1");
            yield return ("union", $"SELECT id FROM {table} UNION ALL SELECT id FROM {table}");
            yield return ("in-subquery", $"SELECT id FROM {table} WHERE id IN (SELECT id FROM {table} WHERE id < 3)");
            yield return ("exists", $"SELECT id FROM {table} a WHERE EXISTS (SELECT 1 FROM {table} b WHERE b.id = a.id)");
            yield return ("cte", $"WITH x AS (SELECT id FROM {table}) SELECT id FROM x");
            yield return ("window", $"SELECT id, row_number() OVER (ORDER BY id) FROM {table}");
            yield return ("self-join", $"SELECT a.id, b.id FROM {table} a JOIN {table} b ON a.id = b.id");
            yield return ("tenant-predicate", $"SELECT id FROM {table} WHERE tenant_id = 'B'");
            yield return ("tautology", $"SELECT id FROM {table} WHERE 1 = 1 OR id = 1");
            yield return ("or-tenant", $"SELECT id FROM {table} WHERE id = 1 OR tenant_id = 'B'");
            yield return ("scalar-subquery", $"SELECT (SELECT count(*) FROM {table}) AS n FROM {table} WHERE id = 1");
            yield return ("derived", $"SELECT d.id FROM (SELECT id FROM {table}) d");
            yield return ("group-having", $"SELECT tenant_id, count(*) FROM {table} GROUP BY tenant_id HAVING count(*) > 0");
            foreach (var (column, _, masked) in columns)
            {
                yield return ("column", $"SELECT {column} FROM {table}");
                yield return ("distinct", $"SELECT DISTINCT {column} FROM {table}");
                yield return ("group", $"SELECT {column}, count(*) FROM {table} GROUP BY {column}");
                yield return ("order", $"SELECT id, {column} FROM {table} ORDER BY id");
                yield return ("is-null", $"SELECT id FROM {table} WHERE {column} IS NOT NULL");
                if (masked)
                {
                    yield return ("masked-predicate", $"SELECT id FROM {table} WHERE {column} = 'x'");
                    yield return ("masked-sort", $"SELECT id FROM {table} ORDER BY {column}");
                    yield return ("masked-function", $"SELECT upper({column}) FROM {table}");
                    yield return ("masked-case", $"SELECT CASE WHEN {column} IS NULL THEN 0 ELSE 1 END FROM {table}");
                }
            }
        }

        yield return ("join-fk", "SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id");
        yield return ("join-left", "SELECT o.id, c.email FROM orders o LEFT JOIN customers c ON o.customer_id = c.id");
        yield return ("join-cross-table", "SELECT e.id, o.status FROM entitlements e JOIN orders o ON e.order_id = o.id");
        yield return ("join-masked", "SELECT o.email, c.email FROM orders o JOIN customers c ON o.customer_id = c.id");
        yield return ("union-masked", "SELECT email FROM orders UNION ALL SELECT email FROM customers");
        yield return ("exists-cross", "SELECT id FROM orders o WHERE EXISTS (SELECT 1 FROM entitlements e WHERE e.order_id = o.id)");
        yield return ("not-in", "SELECT id FROM customers WHERE id NOT IN (SELECT customer_id FROM orders)");
        yield return ("agg-sum", "SELECT status, sum(amount) FROM orders GROUP BY status");
        yield return ("case-expr", "SELECT id, CASE WHEN amount > 100 THEN 'big' ELSE 'small' END FROM orders");
        yield return ("subquery-masked", "SELECT x.email FROM (SELECT email FROM orders) x");
        yield return ("cte-masked", "WITH m AS (SELECT email FROM users) SELECT email FROM m");
        yield return ("except", "SELECT id FROM orders EXCEPT SELECT id FROM customers");
        yield return ("intersect", "SELECT id FROM orders INTERSECT SELECT id FROM customers");
        yield return ("cast", "SELECT CAST(id AS varchar(20)) FROM orders");
        yield return ("system-column", "SELECT ctid FROM orders");
        yield return ("pagination", "SELECT id FROM orders ORDER BY id OFFSET 1 LIMIT 2");
        yield return ("literal-tenant", "SELECT id FROM orders WHERE tenant_id = 'A' OR tenant_id = 'B'");
        yield return ("string-literal-hostile", "SELECT id FROM orders WHERE status = 'it''s'");
        yield return ("window-partition", "SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY id) FROM orders");
        yield return ("lateral-like", "SELECT id FROM orders WHERE note LIKE 'orders-B-%'");
    }

    public static IEnumerable<(string Template, string Sql)> Statements()
    {
        foreach (var (table, columns) in Tables)
        {
            var insertable = columns.Where(c => c.Column != "id").ToArray();
            string names = string.Join(", ", columns.Select(c => c.Column));
            string ValuesFor(string tenant, int id) => string.Join(", ", columns.Select(c => c.Column switch
            {
                "id" => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "tenant_id" => $"'{tenant}'",
                _ => c.Kind switch { 'i' => "1", 'd' => "5.5", 's' => "TIMESTAMP '2024-05-01 10:00:00'", _ => $"'new-{id}'" }
            }));
            yield return ("insert-own", $"INSERT INTO {table} ({names}) VALUES ({ValuesFor("A", 9001)})");
            yield return ("insert-foreign", $"INSERT INTO {table} ({names}) VALUES ({ValuesFor("B", 9002)})");
            yield return ("insert-no-tenant", $"INSERT INTO {table} (id) VALUES (9003)");
            yield return ("insert-select", $"INSERT INTO {table} ({names}) SELECT {names} FROM {table} WHERE id = 1");
            yield return ("delete-own", $"DELETE FROM {table} WHERE id = 2");
            yield return ("delete-foreign", $"DELETE FROM {table} WHERE id = 1001");
            yield return ("delete-hidden", $"DELETE FROM {table} WHERE id = {2001 + (1000 * Array.FindIndex(Tables, t => t.Table == table))}");
            yield return ("delete-range", $"DELETE FROM {table} WHERE id > 0");
            yield return ("delete-unfiltered", $"DELETE FROM {table}");
            yield return ("delete-tautology", $"DELETE FROM {table} WHERE 1 = 1");
            yield return ("update-tenant", $"UPDATE {table} SET tenant_id = 'B' WHERE id = 1");
            yield return ("update-foreign", $"UPDATE {table} SET id = id WHERE id = 1001");
            yield return ("update-subquery", $"UPDATE {table} SET id = id WHERE id IN (SELECT id FROM {table} WHERE id < 3)");
            foreach (var (column, kind, masked) in insertable)
            {
                if (column == "tenant_id") continue;
                string value = kind switch { 'i' => "7", 'd' => "7.5", 's' => "TIMESTAMP '2024-06-01 00:00:00'", _ => "'changed'" };
                yield return (masked ? "update-masked" : "update-column", $"UPDATE {table} SET {column} = {value} WHERE id = 1");
                yield return ("update-range", $"UPDATE {table} SET {column} = {value} WHERE id > 0");
                yield return ("update-foreign-range", $"UPDATE {table} SET {column} = {value} WHERE id > 1000");
            }
        }

        yield return ("update-region-policy-column", "UPDATE orders SET region = 'US' WHERE id = 1");
        yield return ("merge-update", "MERGE INTO orders t USING customers s ON t.customer_id = s.id WHEN MATCHED THEN UPDATE SET status = 'merged'");
        yield return ("merge-insert", "MERGE INTO users t USING customers s ON t.id = s.id WHEN NOT MATCHED THEN INSERT (id, tenant_id, name, status) VALUES (s.id, 'A', s.name, 'x')");
        yield return ("merge-delete", "MERGE INTO employees t USING users s ON t.id = s.id WHEN MATCHED THEN DELETE");
        yield return ("cte-delete", "WITH x AS (SELECT id FROM orders WHERE id < 3) DELETE FROM orders WHERE id IN (SELECT id FROM x)");
    }
}
