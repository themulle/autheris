using BenchmarkDotNet.Attributes;
using TrinoSqlEngine;
using TrinoSqlEngine.Parsing;

namespace Autheris.Benchmarks;

/// <summary>
/// ARCH-MNT-15-SC-11: cold versus warm ANTLR cache. <see cref="ParseWarm"/> parses on a generation that is never replaced; <see cref="ParseCold"/>
/// forces the cap to 1 (a swap on every use), so every parse starts with empty DFA caches. Gated by <c>SqlGate.BenchCheck</c> (cold/warm P99 ratio, ARCH-MNT-15-FU-01). Run: <c>dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlParserCache*"</c>.
/// </summary>
[MemoryDiagnoser]
public class SqlParserCacheBenchmarks
{
    private static readonly string[] Statements =
    [
        "SELECT id, name FROM customers WHERE region = 'EU' AND active = 1",
        "SELECT o.id, c.name, sum(l.qty * l.price) FROM orders o JOIN customers c ON c.id = o.cid JOIN lines l ON l.oid = o.id GROUP BY o.id, c.name HAVING sum(l.qty) > 3",
        "WITH x AS (SELECT id FROM t WHERE a > 1) SELECT CASE WHEN id > 5 THEN 'a' ELSE 'b' END FROM x ORDER BY id LIMIT 20",
        "SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id) AND name LIKE 'a%'"
    ];

    private FastSqlEngine _warm = null!;
    private FastSqlEngine _cold = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _warm = new FastSqlEngine(new AntlrCacheGenerations(new AntlrCacheOptions()));
        _cold = new FastSqlEngine(new AntlrCacheGenerations(
            new AntlrCacheOptions { MaxStatesPerGeneration = 1, GenerationCheckInterval = 1, MinParsesPerGeneration = 1 }));
        foreach (var statement in Statements)
        {
            _warm.Parse(statement.AsMemory());
        }
    }

    [Benchmark(Baseline = true)]
    public object ParseWarm() => _warm.Parse(Statements[_next++ % Statements.Length].AsMemory());

    [Benchmark]
    public object ParseCold() => _cold.Parse(Statements[_next++ % Statements.Length].AsMemory());
}
