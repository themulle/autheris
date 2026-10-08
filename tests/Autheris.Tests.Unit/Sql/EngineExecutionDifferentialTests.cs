namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Wunsch 4, Phase 0: both rewriter engines run the same Trino SQL against SQLite (in memory, seeded, with a tenant row
/// filter) and the result multisets are compared. A text comparison does not work (quoting, aliases differ).
/// Every corpus entry states the expected outcome; when a later phase fixes a construct, its expectation flips to
/// <see cref="Outcome.Same"/> first (red), then the fix makes it green.
/// </summary>
public sealed class EngineExecutionDifferentialTests(ITestOutputHelper output)
{
    public enum Outcome
    {
        /// <summary>Both engines succeed and return the same rows.</summary>
        Same,

        /// <summary>The AST compiler rejects the construct up front (AstBuildException → WebSQL 400).</summary>
        AstRejects,

        /// <summary>Known defect: the AST output fails in the database (WebSQL 500). Must only shrink.</summary>
        AstFailsInDatabase,
    }

    private const string TenantFilter = "tenant_id = 't1'";

    public static IEnumerable<object[]> Corpus() =>
    [
        ["SELECT id, dept FROM orders", Outcome.Same],
        ["SELECT * FROM orders WHERE amount > 100", Outcome.Same],
        ["SELECT dept, SUM(amount) FROM orders GROUP BY dept", Outcome.Same],
        ["SELECT DISTINCT dept FROM orders ORDER BY dept", Outcome.Same],
        ["SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id", Outcome.Same],
        ["SELECT id FROM orders WHERE dept IN (SELECT dept FROM orders WHERE amount > 200)", Outcome.Same],
        ["SELECT id FROM orders ORDER BY amount DESC, id LIMIT 3", Outcome.Same],
        ["SELECT id, CASE WHEN amount > 100 THEN 'big' ELSE 'small' END AS size FROM orders", Outcome.Same],
        ["SELECT dept FROM orders UNION SELECT name FROM customers", Outcome.Same],
        ["WITH big AS (SELECT * FROM orders WHERE amount > 100) SELECT id FROM big", Outcome.Same],
        ["SELECT id FROM orders WHERE dept LIKE 'S%'", Outcome.Same],
        ["SELECT id, CAST(amount AS integer) FROM orders", Outcome.Same],
        ["SELECT id FROM orders WHERE amount BETWEEN 50 AND 150", Outcome.Same],
        ["SELECT id FROM orders o WHERE NOT EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)", Outcome.Same],
        ["SELECT dept, MAX(amount) FROM orders GROUP BY dept HAVING MAX(amount) > 100", Outcome.Same],

        // SQLite tolerates COUNT() and quoted function names; Phase 1 covers them per dialect in emission tests.
        ["SELECT COUNT(*) FROM orders", Outcome.Same],
        ["SELECT dept, COUNT(*) FROM orders GROUP BY dept", Outcome.Same],
        ["SELECT id, COALESCE(dept, 'none') FROM orders", Outcome.Same],

        // Phase 4: window ORDER BY and frames, FILTER
        ["SELECT id, row_number() OVER (PARTITION BY dept ORDER BY amount DESC, id) FROM orders", Outcome.Same],
        ["SELECT id, SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders", Outcome.Same],
        ["SELECT COUNT(id) FILTER (WHERE amount > 100), COUNT(*) FILTER (WHERE dept = 'Sales') FROM orders", Outcome.Same],
        // DATE '…' is not in the corpus: the legacy rewriter passes it through verbatim and SQLite rejects it.
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Engines_AgreeOnResults(string sql, Outcome expected)
    {
        using var connection = SeededDatabase();
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider(TenantFilter),
            TargetDialect = TargetSqlDialect.Sqlite
        };

        string legacySql = engine.RewriteRls(sql.AsMemory(), options);
        var legacyRows = Execute(connection, legacySql);
        output.WriteLine($"legacy: {legacySql}");

        Outcome actual;
        try
        {
            string astSql = engine.GenerateGovernedSql(sql.AsMemory(), options, default);
            output.WriteLine($"ast:    {astSql}");
            List<string> astRows;
            try
            {
                astRows = Execute(connection, astSql);
            }
            catch (SqliteException ex)
            {
                output.WriteLine($"ast db error: {ex.Message}");
                actual = Outcome.AstFailsInDatabase;
                actual.ShouldBe(expected);
                return;
            }

            astRows.ShouldBe(legacyRows, ignoreOrder: !HasOrderBy(sql), customMessage: "AST and legacy return different rows (silent result change).");
            actual = Outcome.Same;
        }
        catch (AstBuildException ex)
        {
            output.WriteLine($"ast rejected: {ex.Message}");
            actual = Outcome.AstRejects;
        }

        actual.ShouldBe(expected);
    }

    private static bool HasOrderBy(string sql) =>
        sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) && !sql.Contains("OVER (", StringComparison.OrdinalIgnoreCase);

    private static List<string> Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL";
            }

            rows.Add(string.Join("|", values));
        }

        return rows;
    }

    private static SqliteConnection SeededDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE orders (id INTEGER, dept TEXT, amount REAL, customer_id INTEGER, created_at TEXT, tenant_id TEXT);
            CREATE TABLE customers (id INTEGER, name TEXT, tenant_id TEXT);
            INSERT INTO orders VALUES
              (1, 'Sales', 120.0, 1, '2024-01-05', 't1'),
              (2, 'Sales', 80.0, 2, '2024-02-10', 't1'),
              (3, 'Finance', 250.0, 1, '2024-03-15', 't1'),
              (4, 'Finance', 40.0, 3, '2024-04-20', 't1'),
              (5, NULL, 150.0, 9, '2024-05-25', 't1'),
              (6, 'HR', 300.0, 2, '2024-06-30', 't1'),
              (7, 'Sales', 999.0, 1, '2024-01-01', 't2'),
              (8, 'Legal', 500.0, 4, '2024-01-01', 't2');
            INSERT INTO customers VALUES (1, 'Anna', 't1'), (2, 'Ben', 't1'), (3, 'Cleo', 't1'), (4, 'Dora', 't2');
            """;
        command.ExecuteNonQuery();
        return connection;
    }
}
