using System.Runtime.CompilerServices;
using System.Text.Json;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace TrinoSqlEngine.Tests.Calibration;

/// <summary>
/// ARCH-MNT-15-FU-02: evidence for the default <see cref="AntlrCacheOptions.MaxStatesPerGeneration"/>. Replays the parser-fuzz seed corpus (and,
/// for the full measurement, the SQL gate, Trino and conformance corpora) through a private generation manager with an effectively unlimited cap
/// and counts states. Deterministic: only state counts and a coarse bytes-per-state estimate, no wall-clock.
/// </summary>
public class AntlrCacheCalibrationTests(ITestOutputHelper output)
{
    /// <summary>Target for worst-case memory of one generation: HardCapFactor x cap x bytes per state.</summary>
    private const long WorstCaseBudgetBytes = 256L * 1024 * 1024;

    private static AntlrCacheGenerations Unlimited() =>
        new(new AntlrCacheOptions { MaxStatesPerGeneration = AntlrCacheOptions.MaxAllowedStatesPerGeneration, MinParsesPerGeneration = 1_000_000 });

    /// <summary>The seed corpus the parser fuzzers replay (tests/Autheris.Tests.Fuzz/corpus), in ordinal file-name order.</summary>
    private static List<string> SeedCorpus()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "Autheris.Tests.Fuzz", "corpus");
        return [.. Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText)];
    }

    private static List<string> FullCorpus()
    {
        var root = RepoRoot();
        var all = SeedCorpus();
        var seen = new HashSet<string>(all, StringComparer.Ordinal);

        void Add(string sql)
        {
            if (sql.Length > 0 && seen.Add(sql))
            {
                all.Add(sql);
            }
        }

        // dql.sql / dml.sql of the SQL compiler gate: one statement block after each "-- @id" header.
        foreach (var file in new[] { "dql.sql", "dml.sql" })
        {
            var text = File.ReadAllText(Path.Combine(root, "tests", "Autheris.Tests.SqlCompilerGate", "Corpus", file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (var block in text.Split("-- @id ", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n').Skip(1);
                Add(string.Join('\n', lines).Trim());
            }
        }

        var fixtures = Path.Combine(root, "tests", "TrinoSqlEngine.Tests", "Fixtures");
        foreach (var file in new[] { "trino_statements.json", "trino_expressions_invalid.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, file)));
            foreach (var item in doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
            {
                Add(item.GetString()!);
            }
        }

        // Parsing is dialect independent (one Trino grammar), so the six dialect files share the same statements; distinct ones only.
        var conformance = Directory.GetFiles(Path.Combine(root, "tests", "TrinoSqlEngine.Tests", "Conformance", "Fixtures"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in conformance)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Cases", out var cases))
            {
                foreach (var c in cases.EnumerateArray())
                {
                    Add(c.GetProperty("Sql").GetString() ?? string.Empty);
                }
            }
        }

        return all;
    }

    private static void ParseAll(FastSqlEngine engine, IEnumerable<string> statements, Action<int>? after = null)
    {
        var i = 0;
        foreach (var sql in statements)
        {
            try
            {
                _ = engine.Parse(sql.AsMemory());
            }
            catch (ParseCanceledException)
            {
                // Rejected input still grows the caches, which is what the measurement wants.
            }

            after?.Invoke(++i);
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_ReportsStateGrowthOfTheCorpus()
    {
        var corpus = FullCorpus();

        // Warm up the static ATN and JIT on a throwaway manager so the byte estimate sees generation data only.
        using (var warm = Unlimited())
        {
            ParseAll(new FastSqlEngine(warm), corpus.Take(20));
        }

        using var generations = Unlimited();
        var engine = new FastSqlEngine(generations);
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var checkpoints = new[] { 10, 25, 50, 100 }.ToDictionary(p => Math.Max(1, corpus.Count * p / 100), p => p);
        var table = new List<string>();
        long previous = 0;
        long maxIncrease = 0;
        ParseAll(engine, corpus, n =>
        {
            long states = AntlrCacheGeneration.CountStates(generations.Current);
            maxIncrease = Math.Max(maxIncrease, states - previous);
            previous = states;
            if (checkpoints.TryGetValue(n, out var pct))
            {
                table.Add($"{pct,3}% ({n,5} stmts): {states,8} states");
            }
        });

        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(generations);
        var total = AntlrCacheGeneration.CountStates(generations.Current);
        var bytesPerState = (after - before) / Math.Max(1, total);

        Assert.True(corpus.Count >= 2000 && SeedCorpus().Count >= 300, "The corpus shape changed; re-check the calibration.");
        output.WriteLine($"corpus statements: {corpus.Count} (seed corpus: {SeedCorpus().Count})");
        table.ForEach(output.WriteLine);
        output.WriteLine($"total states: {total}; max per-statement increase: {maxIncrease}; swaps: {generations.SwapCount}");
        // GC.GetTotalMemory is process-global (other threads allocate too), so the bytes per state value is approximate.
        output.WriteLine($"managed bytes: {after - before}; approx bytes per state: {bytesPerState}");
        var defaults = new AntlrCacheOptions();
        output.WriteLine($"default cap {defaults.MaxStatesPerGeneration} -> worst case {(long)defaults.MaxStatesPerGeneration * defaults.HardCapFactor * bytesPerState / (1024 * 1024)} MB (budget {WorstCaseBudgetBytes / (1024 * 1024)} MB)");
        output.WriteLine($"headroom of the default cap over the corpus steady state: x{(double)defaults.MaxStatesPerGeneration / total:F1}");

        using var seedOnly = Unlimited();
        ParseAll(new FastSqlEngine(seedOnly), SeedCorpus());
        output.WriteLine($"seed corpus only: {AntlrCacheGeneration.CountStates(seedOnly.Current)} states");

        Assert.Equal(0, generations.SwapCount);
        Assert.True(total > 0);
    }

    [Fact]
    public void SeedCorpus_FitsInOneGenerationAtTheDefaultCap_WithoutASwap()
    {
        var defaults = new AntlrCacheOptions { MinParsesPerGeneration = 1, GenerationCheckInterval = 1 };
        using var generations = new AntlrCacheGenerations(defaults);
        var engine = new FastSqlEngine(generations);
        var corpus = SeedCorpus();

        ParseAll(engine, corpus);

        long states = AntlrCacheGeneration.CountStates(generations.Current);
        Assert.True(corpus.Count >= 300, "The seed corpus is expected to hold at least 300 statements.");
        Assert.Equal(0, generations.SwapCount);
        // Headroom: the whole corpus must stay well under the cap so a realistic workload never swaps in steady state.
        Assert.True(states * CorpusHeadroomFactor <= defaults.MaxStatesPerGeneration, $"Seed corpus used {states} states; default cap {defaults.MaxStatesPerGeneration}.");
    }

    private const int CorpusHeadroomFactor = 2;

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var dir = new FileInfo(file).Directory;
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Autheris.sln not found.");
    }
}
