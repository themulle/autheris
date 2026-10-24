using System.Text;
using TrinoSqlEngine;

namespace Autheris.Tests.Fuzz.Replay;

/// <summary>
/// The seed corpus (SEC-SIM-13): a deterministic sample of the statements of our own <c>dql.sql</c> (project-licensed) plus the
/// hand-written grammar seeds below. No third-party text. Seeds are at most 4 KB, LF only, no trailing newline.
/// </summary>
public static class SeedCorpus
{
    public const int MaxSeedBytes = 4096;
    public const int MaxDqlSeeds = 240;

    public const string DqlRelativePath = "tests/Autheris.Tests.SqlCompilerGate/Corpus/dql.sql";

    /// <summary>Statement blocks of <c>dql.sql</c> (<c>-- @id &lt;id&gt; ... source=&lt;source&gt;</c> header, body, blank line).</summary>
    public static IReadOnlyList<(string Id, string Source, string Sql)> ParseDql(string text)
    {
        var result = new List<(string, string, string)>();
        string? id = null;
        string source = string.Empty;
        var body = new List<string>();

        void Flush()
        {
            if (id is not null)
            {
                var sql = string.Join('\n', body).Trim();
                if (sql.Length > 0)
                {
                    result.Add((id, source, sql));
                }
            }

            id = null;
            body.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("-- @id ", StringComparison.Ordinal))
            {
                Flush();
                var parts = line["-- @id ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                id = parts[0];
                source = parts.FirstOrDefault(p => p.StartsWith("source=", StringComparison.Ordinal))?["source=".Length..] ?? string.Empty;
            }
            else if (id is not null)
            {
                body.Add(line);
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// The seed set as (file name, content). Deterministic: dql statements sorted by (source, id), every k-th taken so that at most
    /// <see cref="MaxDqlSeeds"/> remain, then the hand-written seeds.
    /// </summary>
    public static IReadOnlyList<(string Name, string Content)> Build(string dqlText)
    {
        var statements = ParseDql(dqlText)
            .Where(s => Encoding.UTF8.GetByteCount(s.Sql) <= MaxSeedBytes && !s.Sql.Contains('\r', StringComparison.Ordinal))
            .DistinctBy(s => s.Sql, StringComparer.Ordinal)
            .OrderBy(s => s.Source, StringComparer.Ordinal)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        var stride = Math.Max(1, (statements.Count + MaxDqlSeeds - 1) / MaxDqlSeeds);
        var seeds = new List<(string, string)>();
        for (var i = 0; i < statements.Count; i += stride)
        {
            seeds.Add(("dql-" + statements[i].Id + ".sql", statements[i].Sql));
        }

        for (var i = 0; i < HandWritten.Length; i++)
        {
            seeds.Add(($"hand-{i + 1:D2}.sql", HandWritten[i]));
        }

        return seeds;
    }

    public static void Export(string dqlPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var (name, content) in Build(File.ReadAllText(dqlPath)))
        {
            File.WriteAllText(Path.Combine(outDir, name), content, new UTF8Encoding(false));
        }
    }

    /// <summary>AFL dictionary: the keyword literals of the engine grammar plus punctuation fragments.</summary>
    public static string BuildDictionary()
    {
        var words = new SortedSet<string>(StringComparer.Ordinal);
        var vocabulary = SqlBaseLexer.DefaultVocabulary;
        for (var i = 0; i < 512; i++)
        {
            var literal = vocabulary.GetLiteralName(i);
            if (literal is { Length: > 2 } && literal[0] == '\'' && literal[^1] == '\'')
            {
                words.Add(literal[1..^1]);
            }
        }

        var sb = new StringBuilder();
        var n = 0;
        foreach (var word in words)
        {
            sb.Append("kw").Append(n++.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append("=\"").Append(Escape(word)).Append("\"\n");
        }

        foreach (var fragment in new[] { "'", "\"", "(", ")", ",", ";", "*", "/*", "*/", "--", "||", "<=", ">=", "<>", "::", "$$", "[", "]", "->", "\\" })
        {
            sb.Append('p').Append(n++.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append("=\"").Append(Escape(fragment)).Append("\"\n");
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\x22", StringComparison.Ordinal);

    /// <summary>Hand-written grammar seeds (original text).</summary>
    public static readonly string[] HandWritten =
    [
        "SELECT 1",
        "SELECT id, status FROM orders WHERE id > 10 ORDER BY id DESC LIMIT 5",
        "SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer_id WHERE c.region = 'EU'",
        "SELECT o.id FROM orders o LEFT JOIN entitlements e ON e.order_id = o.id WHERE e.id IS NULL",
        "SELECT status, count(*), sum(amount) FROM orders GROUP BY status HAVING count(*) > 1",
        "SELECT dept, avg(salary) OVER (PARTITION BY dept ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM employees",
        "SELECT id FROM orders UNION ALL SELECT id FROM customers",
        "SELECT id FROM orders INTERSECT SELECT id FROM customers EXCEPT SELECT id FROM users",
        "WITH t AS (SELECT id FROM orders) SELECT * FROM t WHERE id < 100",
        "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 10) SELECT n FROM r",
        "SELECT CASE WHEN amount > 100 THEN 'big' WHEN amount > 10 THEN 'mid' ELSE 'small' END FROM orders",
        "SELECT CAST(id AS varchar(20)), CAST(amount AS decimal(10, 2)) FROM orders",
        "SELECT id FROM orders WHERE status IN ('open', 'closed') AND amount BETWEEN 1 AND 99",
        "SELECT id FROM orders WHERE note LIKE 'a%' ESCAPE '!' OR note NOT LIKE '%z'",
        "SELECT id FROM orders o WHERE EXISTS (SELECT 1 FROM entitlements e WHERE e.order_id = o.id)",
        "SELECT id FROM orders WHERE customer_id IN (SELECT id FROM customers WHERE region = 'US')",
        "SELECT (SELECT max(amount) FROM orders) AS m",
        "SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS t(x, y)",
        "SELECT x FROM UNNEST(ARRAY[1, 2, 3]) AS t(x)",
        "SELECT ARRAY[1, 2][1], ROW(1, 'a'), MAP(ARRAY['k'], ARRAY[1])",
        "SELECT created_at AT TIME ZONE 'UTC' FROM orders",
        "SELECT TIMESTAMP '2020-01-01 00:00:00', DATE '2020-01-01', INTERVAL '1' DAY",
        "SELECT \"id\", \"tenant_id\" FROM \"orders\"",
        "SELECT /* c */ id FROM orders -- trailing",
        "SELECT id FROM orders ORDER BY amount DESC NULLS LAST OFFSET 2 ROWS FETCH FIRST 3 ROWS ONLY",
        "SELECT lower(name), upper(name), length(name), substr(name, 1, 3), coalesce(name, 'x') FROM customers",
        "SELECT try(CAST(note AS integer)), nullif(id, 0), if(id > 1, 'a', 'b') FROM orders",
        "SELECT regexp_like(note, '^a.*z$'), concat(note, 'x'), 'a' || 'b' FROM orders",
        "SELECT id FROM orders TABLESAMPLE BERNOULLI (10)",
        "SELECT o.id, t.x FROM orders o CROSS JOIN LATERAL (SELECT o.id + 1 AS x) t",
        "SELECT status, region, count(*) FROM orders GROUP BY GROUPING SETS ((status), (region), ())",
        "SELECT DISTINCT status FROM orders",
        "SELECT count(DISTINCT customer_id), min(amount), max(amount), approx_distinct(id) FROM orders",
        "SELECT transform(ARRAY[1, 2], x -> x + 1)",
        "SELECT id FROM orders WHERE amount IS NOT DISTINCT FROM 5",
        "SELECT email FROM customers",
        "SELECT * FROM orders",
        "SELECT o.* FROM orders o",
        "SELECT t.* FROM (SELECT * FROM users) t",
        "SELECT 1 + 2 * 3 - 4 / 5 % 6",
        "SELECT NOT TRUE AND FALSE OR NULL",
        "SELECT -id, +id FROM orders",
        "SELECT 1.5e3, 0x1F, 12345678901234567890",
        "SELECT 'it''s', U&'\\0041'",
        "INSERT INTO users (id, name, status) VALUES (1, 'a', 'open')",
        "INSERT INTO users (id, name) SELECT id, name FROM customers",
        "UPDATE orders SET status = 'closed' WHERE id = 1",
        "UPDATE employees SET salary = salary + 1 WHERE dept = 'it'",
        "DELETE FROM entitlements WHERE order_id = 1",
        "MERGE INTO users u USING customers c ON u.id = c.id WHEN MATCHED THEN UPDATE SET name = c.name WHEN NOT MATCHED THEN INSERT (id, name) VALUES (c.id, c.name)",
        "SELECT",
        "SELECT FROM",
        "SELECT * FROM",
        "((((((((((SELECT 1))))))))))",
        "SELECT 'unterminated",
        "SELECT \"unterminated",
        "SELECT /* unterminated",
        "SELECT $$ dollar $$",
        "SELECT E'esc\\n'",
        "DROP TABLE orders",
        "SELECT * FROM unknown_table",
        "SELECT id FROM orders; SELECT 2",
        "EXPLAIN SELECT 1",
        "SHOW TABLES",
        "SELECT * FROM TABLE(some_function(1))",
        "SELECT ?, :p, $1 FROM orders",
        "SELECT a.b.c.d FROM orders",
        "SELECT count(*) FILTER (WHERE amount > 1) FROM orders",
        "SELECT first_value(id) OVER w FROM orders WINDOW w AS (PARTITION BY status ORDER BY id)",
    ];
}
