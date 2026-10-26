using System.Diagnostics.Metrics;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using TrinoSqlEngine.Parsing;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// ARCH-MNT-15 (plan 2026-10-26, section 3.1): the ANTLR DFA and prediction-context caches are process-wide statics that only grow. They are
/// replaced by generations: one immutable reference per cache set, swapped when the state count passes a cap, never cleared in place.
/// </summary>
[Trait("Category", "Security")]
public class AntlrCacheGenerationTests
{
    private static readonly string[] ValidCorpus =
    [
        "SELECT 1",
        "SELECT a, b FROM t WHERE a > 1 AND b IN (1, 2, 3)",
        "SELECT CASE WHEN a = 1 THEN 'x' WHEN a = 2 THEN 'y' ELSE 'z' END FROM t",
        "SELECT o.id, c.name FROM orders o JOIN customers c ON o.cid = c.id WHERE o.total BETWEEN 10 AND 20",
        "SELECT a, count(*) FROM t GROUP BY a HAVING count(*) > 2 ORDER BY a DESC LIMIT 10",
        "WITH x AS (SELECT id FROM t) SELECT * FROM x UNION ALL SELECT id FROM u",
        "SELECT * FROM t WHERE name LIKE 'a%' OR name IS NULL",
        "SELECT CAST(a AS VARCHAR), coalesce(b, 0) FROM t",
        "SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)",
        "SELECT sum(a) OVER (PARTITION BY b ORDER BY c) FROM t",
        "INSERT INTO t (a, b) VALUES (1, 2)",
        "UPDATE t SET a = 1 WHERE id = 2",
        "DELETE FROM t WHERE id = 3",
        "SELECT a FROM t WHERE b = ?",
        "SELECT 1 + 2 * (3 - 4) / 5"
    ];

    private static readonly string[] InvalidCorpus =
    [
        "SELECT FROM",
        "SELECT a FROM t WHERE",
        "SELEC 1",
        "SELECT (1",
        "SELECT a b c FROM t",
        "FROM t SELECT a",
        "SELECT 'unterminated",
        "SELECT * FROM t WHERE a = = 1"
    ];

    private static AntlrCacheGenerations Generations(int cap, int interval, int minParses, Func<AntlrCacheGeneration, long>? counter = null) =>
        new(new AntlrCacheOptions { MaxStatesPerGeneration = cap, GenerationCheckInterval = interval, MinParsesPerGeneration = minParses }, counter);

    private static AntlrCacheGenerations EverySwap() => Generations(1, 1, 1);

    private static string Outcome(FastSqlEngine engine, string sql)
    {
        try
        {
            var (tree, _) = engine.Parse(sql.AsMemory());
            return "OK:" + Trees.ToStringTree(tree, SqlBaseParser.ruleNames);
        }
        catch (ParseCanceledException ex)
        {
            return "ERR:" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    private static List<string> RunCorpus(FastSqlEngine engine) =>
        [.. ValidCorpus.Concat(InvalidCorpus).Select(s => Outcome(engine, s))];

    private static string DistinctStatement(Random random, int i)
    {
        int depth = random.Next(1, 5);
        string expr = $"a{i}";
        for (int d = 0; d < depth; d++)
        {
            expr = random.Next(4) switch
            {
                0 => $"CASE WHEN {expr} > {i + d} THEN {d} ELSE {i} END",
                1 => $"coalesce({expr}, {i * 3 + d})",
                2 => $"({expr} + b{d} * {i})",
                _ => $"CAST({expr} AS VARCHAR)"
            };
        }

        string join = random.Next(3) == 0 ? $" JOIN u{i % 5} ON t{i % 7}.id = u{i % 5}.id" : string.Empty;
        string where = random.Next(2) == 0 ? $" WHERE c{i} IN ({i}, {i + 1}) AND d{i} LIKE 'v{i}%'" : string.Empty;
        return $"SELECT {expr} AS x{i} FROM t{i % 7}{join}{where}";
    }

    // ---- T-01 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_CorpusTreesAndErrors_IdenticalAcrossWarmColdAndSwappedGenerations()
    {
        var cold = RunCorpus(new FastSqlEngine(Generations(1_000_000, 32, 1)));
        var warmEngine = new FastSqlEngine(Generations(1_000_000, 32, 1));
        RunCorpus(warmEngine);
        var warm = RunCorpus(warmEngine);
        var swapGenerations = EverySwap();
        var swapped = RunCorpus(new FastSqlEngine(swapGenerations));

        Assert.Contains(cold, o => o.StartsWith("OK:", StringComparison.Ordinal));
        Assert.Contains(cold, o => o.StartsWith("ERR:", StringComparison.Ordinal));
        Assert.Equal(cold, warm);
        Assert.Equal(cold, swapped);
        Assert.True(swapGenerations.SwapCount >= 1, "ARCH-MNT-15-T-01: the swapped run must really have swapped.");
    }

    // ---- T-02 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Generation_StateCount_StaysBoundedForDistinctStatements()
    {
        const int cap = 3_000;
        const int slackPerParse = 4_000;
        var observed = new List<long>();
        var generations = Generations(cap, 1, 1, g =>
        {
            long count = AntlrCacheGeneration.CountStates(g);
            lock (observed)
            {
                observed.Add(count);
            }

            return count;
        });
        var engine = new FastSqlEngine(generations);
        var random = new Random(15_2026);

        for (int i = 0; i < 1_500; i++)
        {
            engine.Parse(DistinctStatement(random, i).AsMemory());
        }

        Assert.True(generations.SwapCount >= 1);
        Assert.True(observed.Count > 0);
        Assert.True(observed.Max() <= cap + slackPerParse, $"max observed state count {observed.Max()} exceeds cap + slack.");
    }

    // ---- T-03 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Generation_ConcurrentParsesDuringSwap_NoExceptionsAndEquivalentResults()
    {
        var baseline = RunCorpus(new FastSqlEngine(Generations(1_000_000, 32, 1)));
        var generations = EverySwap();
        var engine = new FastSqlEngine(generations);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var results = new List<List<string>>();
            for (int round = 0; round < 3; round++)
            {
                results.Add(RunCorpus(engine));
            }

            return results;
        })).ToArray();

        var all = await Task.WhenAll(tasks);

        foreach (var rounds in all)
        {
            foreach (var result in rounds)
            {
                Assert.Equal(baseline, result);
            }
        }

        Assert.True(generations.SwapCount >= 1);
    }

    // ---- T-04 ------------------------------------------------------------------------------------------------------------

    [Trait("Category", "Unit")]
    [Fact]
    public void Generation_OldGenerationNotCleared_WhileParseInFlight()
    {
        var generations = Generations(1, 1, 1);
        var (weak, statesWhileHeld, tokenTypes) = HoldAndSwap(generations);

        // the lexer was still usable after the swap, and the old generation was never cleared in place
        Assert.True(statesWhileHeld > 0, "the old generation must keep its states while a lexer references it");
        Assert.NotEmpty(tokenTypes);

        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive, "generation 1 must be collectable once the last in-flight user dropped it");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, long States, List<int> Types) HoldAndSwap(AntlrCacheGenerations generations)
    {
        var first = generations.Current;
        var weak = new WeakReference(first);
        var lexer = generations.CreateLexer(new ZeroCopyCaseInsensitiveStream("SELECT a FROM t".AsMemory()));
        Assert.Same(first, lexer.CacheGeneration);

        // force swaps while the lexer (an in-flight user) still holds generation 1
        for (int i = 0; i < 3; i++)
        {
            generations.CreateLexer(new ZeroCopyCaseInsensitiveStream("SELECT 1".AsMemory())).GetAllTokens();
        }

        Assert.NotSame(first, generations.Current);
        var types = lexer.GetAllTokens().Select(t => t.Type).ToList();
        long states = AntlrCacheGeneration.CountStates(first);
        lexer = null!;
        first = null!;
        return (weak, states, types);
    }

    // ---- T-05 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Swap_FaultInjected_RejectsParse_DoesNotReturnInstanceToPool()
    {
        bool fail = false;
        var generations = Generations(1_000_000, 1, 1, g => fail ? throw new InvalidOperationException("injected") : AntlrCacheGeneration.CountStates(g));
        var engine = new FastSqlEngine(generations);
        engine.Parse("SELECT 1".AsMemory()); // healthy

        fail = true;
        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("SELECT 1".AsMemory()));
        Assert.ThrowsAny<ParseCanceledException>(() => generations.CreateLexer(new ZeroCopyCaseInsensitiveStream("SELECT 1".AsMemory())));

        var policy = new ParserPooledObjectPolicy(generations);
        var parser = policy.Create();
        Assert.Same(generations.Current, parser.CacheGeneration);
        Assert.ThrowsAny<ParseCanceledException>(() => policy.Return(parser));

        fail = false;
        engine.Parse("SELECT 1".AsMemory()); // the engine recovers: nothing was left half bound
    }

    // ---- T-06 ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void AstSecurityVisitor_RunsOnEveryParse_IncludingFirstParseAfterSwap(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.MaskEmail();
        var generations = EverySwap();
        var engine = new FastSqlEngine(generations);
        const string forbidden = "DELETE FROM orders WHERE orders IS NOT NULL AND id = 1";

        for (int i = 0; i < 4; i++)
        {
            long before = generations.SwapCount;
            Assert.ThrowsAny<SecurityException>(() => engine.Compile(forbidden.AsMemory(), env.Request(), CancellationToken.None));
            Assert.True(generations.SwapCount > before, "the rejected parse must be the one that swapped");
            Assert.ThrowsAny<SecurityException>(() => engine.Compile(forbidden.AsMemory(), env.Request(), CancellationToken.None));
        }
    }

    // ---- T-07 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parser_AfterRebind_UsesSllModeAndSameListeners()
    {
        var generations = EverySwap();
        var policy = new ParserPooledObjectPolicy(generations);
        var parser = policy.Create();
        var first = parser.CacheGeneration;
        var stale = policy.Create();

        // dirty it like the LL fallback does, then force a swap
        parser.Interpreter.PredictionMode = PredictionMode.LL;
        parser.ErrorHandler = new DefaultErrorStrategy();
        parser.AddErrorListener(ConsoleErrorListener<IToken>.Instance);
        for (int i = 0; i < 3; i++)
        {
            generations.CreateLexer(new ZeroCopyCaseInsensitiveStream("SELECT 1".AsMemory())).GetAllTokens();
        }

        Assert.NotSame(first, generations.Current);

        policy.PrepareForUse(parser);

        Assert.Same(generations.Current, parser.CacheGeneration);
        Assert.Equal(PredictionMode.SLL, parser.Interpreter.PredictionMode);
        Assert.IsType<BailErrorStrategy>(parser.ErrorHandler);
        Assert.Empty(parser.ErrorListeners);

        // a stale parser is discarded instead of going back to the pool, a current one is kept and never leaves in LL mode
        Assert.False(policy.Return(stale));
        var fresh = policy.Create();
        fresh.Interpreter.PredictionMode = PredictionMode.LL;
        Assert.True(policy.Return(fresh) || fresh.CacheGeneration != generations.Current);
        Assert.Equal(PredictionMode.SLL, fresh.Interpreter.PredictionMode);
    }

    // ---- T-09 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SwapMetricsAndLogs_ContainNoSqlTextOrTenantIdentifiers()
    {
        const string marker = "zz_unique_marker_tenant_8f3a";
        var seen = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AntlrCacheGenerations.MeterName)
            {
                seen.Add("instrument:" + instrument.Name);
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(seen, i, tags));
        listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(seen, i, tags));
        listener.Start();

        var generations = EverySwap();
        var engine = new FastSqlEngine(generations);
        for (int i = 0; i < 3; i++)
        {
            engine.Parse($"SELECT '{marker}' AS {marker}_{i} FROM {marker}".AsMemory());
        }

        listener.RecordObservableInstruments();

        Assert.NotEmpty(seen);
        Assert.DoesNotContain(seen, s => s.Contains(marker, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seen, s => s.StartsWith("tag:", StringComparison.Ordinal)); // fixed low-cardinality set: here none at all
        Assert.Contains(seen, s => s.Contains("swap", StringComparison.OrdinalIgnoreCase));

        static void Record(List<string> sink, Instrument i, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            sink.Add("measurement:" + i.Name);
            foreach (var tag in tags)
            {
                sink.Add("tag:" + tag.Key + "=" + tag.Value);
            }
        }
    }

    // ---- T-10 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Swap_AdversarialDistinctStatements_RespectsMinParsesPerGeneration()
    {
        const int minParses = 50;
        const int parses = 500;
        var generations = Generations(1, 1, minParses);
        var engine = new FastSqlEngine(generations);
        var random = new Random(10_2026);

        for (int i = 0; i < parses; i++)
        {
            engine.Parse(DistinctStatement(random, i).AsMemory());
        }

        // at most two cache uses per parse (lexer and parser); a swap needs minParses uses of the generation it replaces
        long maxSwaps = parses * 2L / minParses;
        Assert.InRange(generations.SwapCount, 1, maxSwaps);
    }
}
