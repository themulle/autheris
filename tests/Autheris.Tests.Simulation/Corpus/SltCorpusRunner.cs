using System.Data.Common;
using System.Text;
using Autheris.Tests.Simulation.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>The outcome of one corpus run: counts per scenario and verdict, plus the first finding of every error class.</summary>
public sealed class SltRunSummary
{
    public Dictionary<SltScenario, int> Executed { get; } = [];

    public Dictionary<(SltScenario Scenario, SltVerdict Verdict), int> Verdicts { get; } = [];

    public int HarnessMismatches { get; set; }

    public int ReferenceAgreed { get; set; }

    public int HangLimit { get; set; } = SltCorpusRunner.HangCircuitLimit;

    /// <summary>Hangs per (file, error class); a file whose count reaches <see cref="HangLimit"/> is skipped from then on.</summary>
    public Dictionary<(string File, string Class), int> HangCounts { get; } = [];

    public Dictionary<string, string> OpenCircuits { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

    public int SampledQueries { get; set; }

    public Dictionary<string, (SltVerdict Verdict, SimFinding Finding, string File, int Line)> FirstByClass { get; } = new(StringComparer.Ordinal);

    public SimResults Results { get; } = new();

    public int Count(SltVerdict verdict) => Verdicts.Where(p => p.Key.Verdict == verdict).Sum(p => p.Value);

    public int TotalExecuted => Executed.Values.Sum();
}

/// <summary>
/// Runs the sampled queries of the manifest files over scenario A (all files) and B (select1-3). Setup statements run in full and in order on the
/// reference file; a query runs only when sampled. The reference result must equal the recorded result, otherwise the query is a
/// <see cref="SltVerdict.HarnessMismatch"/> and is not compared (Autheris is never the expected source).
/// </summary>
public static class SltCorpusRunner
{
    /// <summary>After this many hangs of one error class in one file, the remaining queries of that file are skipped (the job stays red).</summary>
    public const int HangCircuitLimit = 40;

    private static readonly string[] RowFilterFiles = ["test/select1.test", "test/select2.test", "test/select3.test"];

    public static IReadOnlyList<SltQuery> AllQueries(IReadOnlyDictionary<string, IReadOnlyList<SltRecord>> corpus) =>
    [
        .. corpus.Values.SelectMany(r => r).OfType<SltQuery>().Where(q => SltDialectFilter.Applies(q)),
    ];

    public static async Task<SltRunSummary> RunAsync(
        IReadOnlyDictionary<string, IReadOnlyList<SltRecord>> corpus,
        long seed,
        int sampleSize,
        SltClassifier classifier,
        string reproPrefix,
        bool referenceOnly = false,
        Func<SltScenario, DbConnection, ISltEngine>? engineFactory = null,
        int hangCircuitLimit = HangCircuitLimit)
    {
        var summary = new SltRunSummary { HangLimit = hangCircuitLimit };
        engineFactory ??= static (scenario, connection) => new SltEngineRunner(scenario, connection);
        var sampled = SltSampler.Select(AllQueries(corpus), seed, sampleSize);
        summary.SampledQueries = sampled.Count;
        var sampledIds = sampled.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (file, records) in corpus.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!records.OfType<SltQuery>().Any(q => sampledIds.Contains(q.Id)))
            {
                continue;
            }

            await RunFileAsync(file, records, sampledIds, classifier, summary, reproPrefix, referenceOnly, engineFactory);
        }

        summary.Results.AddNote("Executed", "scenario:A", summary.Executed.GetValueOrDefault(SltScenario.Permissive), $"sample={sampleSize};seed={seed};sampled={sampled.Count}");
        summary.Results.AddNote("Executed", "scenario:B", summary.Executed.GetValueOrDefault(SltScenario.RowFilter), $"sample={sampleSize};seed={seed};sampled={sampled.Count}");
        foreach (var verdict in new[] { SltVerdict.Equal, SltVerdict.AcceptedDifference, SltVerdict.UnsupportedByDesign })
        {
            summary.Results.AddNote(verdict.ToString(), "all-scenarios", summary.Count(verdict), "n/a");
        }

        summary.Results.AddNote("HarnessMismatch", "reference-disagrees-with-recorded", summary.HarnessMismatches, "n/a");
        foreach (var (openFile, cls) in summary.OpenCircuits.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var skipped = summary.Skipped.GetValueOrDefault(openFile);
            summary.Results.AddNote("CircuitOpen", $"{cls} circuit open: {skipped} skipped", skipped, $"file={openFile};limit={hangCircuitLimit}");
        }

        return summary;
    }

    private static async Task RunFileAsync(string file, IReadOnlyList<SltRecord> records, HashSet<string> sampledIds, SltClassifier classifier, SltRunSummary summary, string reproPrefix, bool referenceOnly, Func<SltScenario, DbConnection, ISltEngine> engineFactory)
    {
        await using var reference = SltReference.Create();
        SltReference? filtered = null;
        SqliteConnection? engineConnection = null;
        ISltEngine? runnerA = null;
        ISltEngine? runnerB = null;
        var rowFilterFile = RowFilterFiles.Contains(file, StringComparer.Ordinal);
        try
        {
            foreach (var record in records)
            {
                if (!SltDialectFilter.Applies(record))
                {
                    continue;
                }

                switch (record)
                {
                    case SltHalt:
                        return;
                    case SltStatement s:
                        if (filtered is not null)
                        {
                            throw new InvalidOperationException($"{file}:{s.Line}: statement after a query; the row-filter reference copy would be stale.");
                        }

                        await RunStatementAsync(reference, s, summary);
                        runnerA?.InvalidateCatalog();
                        runnerB?.InvalidateCatalog();
                        break;
                    case SltQuery q when sampledIds.Contains(q.Id):
                        engineConnection ??= SltReference.OpenConnection(reference.Path, readOnly: true);
                        runnerA ??= engineFactory(SltScenario.Permissive, engineConnection);
                        if (rowFilterFile && filtered is null && !referenceOnly)
                        {
                            filtered = reference.Clone();
                            await filtered.ExecuteAsync($"DELETE FROM {SltEngineRunner.FilteredTable} WHERE {SltEngineRunner.FilterColumn} IS NULL OR NOT ({SltEngineRunner.FilterColumn} > {SltEngineRunner.FilterMinExclusive})");
                            runnerB = engineFactory(SltScenario.RowFilter, engineConnection);
                        }

                        await RunQueryAsync(q, reference, filtered, runnerA, runnerB, classifier, summary, reproPrefix, referenceOnly);
                        break;
                }
            }
        }
        finally
        {
            engineConnection?.Dispose();
            if (filtered is not null)
            {
                await filtered.DisposeAsync();
            }
        }
    }

    private static async Task RunStatementAsync(SltReference reference, SltStatement s, SltRunSummary summary)
    {
        try
        {
            await reference.ExecuteAsync(s.Sql);
            if (!s.ExpectOk)
            {
                summary.HarnessMismatches++;
            }
        }
        catch (Exception ex) when (ex is SqliteException or SltReferenceRejectedException or TimeoutException)
        {
            if (s.ExpectOk)
            {
                summary.HarnessMismatches++;
            }
        }
    }

    private static async Task RunQueryAsync(SltQuery q, SltReference reference, SltReference? filtered, ISltEngine runnerA, ISltEngine? runnerB, SltClassifier classifier, SltRunSummary summary, string reproPrefix, bool referenceOnly)
    {
        if (!referenceOnly && summary.OpenCircuits.ContainsKey(q.File))
        {
            summary.Skipped[q.File] = summary.Skipped.GetValueOrDefault(q.File) + 1;
            return;
        }

        IReadOnlyList<string> expected;
        try
        {
            expected = SltResultFormatter.Format(await reference.RunAsync(q.Sql), q.Types, q.Sort);
        }
        catch (Exception ex) when (ex is SqliteException or SltReferenceRejectedException or InvalidOperationException or TimeoutException)
        {
            summary.HarnessMismatches++;
            return;
        }

        if (!SltResultFormatter.Matches(expected, q.Expected))
        {
            summary.HarnessMismatches++;
            return;
        }

        summary.ReferenceAgreed++;
        if (referenceOnly)
        {
            return;
        }

        Record(q, SltScenario.Permissive, runnerA.Run(q.Sql), expected, null, classifier, summary, reproPrefix);

        if (runnerB is not null && filtered is not null)
        {
            IReadOnlyList<string> expectedB;
            try
            {
                expectedB = SltResultFormatter.Format(await filtered.RunAsync(q.Sql), q.Types, q.Sort);
            }
            catch (Exception ex) when (ex is SqliteException or SltReferenceRejectedException or InvalidOperationException or TimeoutException)
            {
                summary.HarnessMismatches++;
                return;
            }

            Record(q, SltScenario.RowFilter, runnerB.Run(q.Sql), expectedB, expected, classifier, summary, reproPrefix);
        }
    }

    private static void Record(SltQuery q, SltScenario scenario, SltEngineOutcome outcome, IReadOnlyList<string> expected, IReadOnlyList<string>? unfiltered, SltClassifier classifier, SltRunSummary summary, string reproPrefix)
    {
        summary.Executed[scenario] = summary.Executed.GetValueOrDefault(scenario) + 1;
        var observation = SltComparison.Observe(outcome, q, scenario, expected, unfiltered, out var actualValues);
        var verdict = classifier.Classify(observation);
        summary.Verdicts[(scenario, verdict)] = summary.Verdicts.GetValueOrDefault((scenario, verdict)) + 1;
        if (observation.Kind is null)
        {
            return;
        }

        var finding = new SimFinding(
            observation.Kind.Value,
            observation.ErrorClass,
            q.Sql,
            Preview(expected),
            outcome is SltEngineRows ? Preview(actualValues) : Describe(outcome),
            $"{reproPrefix} # scenario {SltComparison.Name(scenario)}, query {q.Id} at {q.File}:{q.Line}");
        summary.Results.Add(finding, verdict.ToString());
        if (verdict == SltVerdict.Hang)
        {
            var key = (q.File, observation.ErrorClass);
            var hangs = summary.HangCounts.GetValueOrDefault(key) + 1;
            summary.HangCounts[key] = hangs;
            if (hangs >= summary.HangLimit)
            {
                summary.OpenCircuits.TryAdd(q.File, observation.ErrorClass);
            }
        }

        summary.FirstByClass.TryAdd(observation.ErrorClass, (verdict, finding, q.File, q.Line));
    }

    private static string Describe(SltEngineOutcome outcome) => outcome switch
    {
        SltEngineRejected r => $"rejected at {r.Stage}: {r.ExceptionType}: {r.Message}",
        SltEngineCrashed c => $"crashed at {c.Stage}: {c.ExceptionType}: {c.Message}",
        SltEngineHung h => $"hung at {h.Stage}",
        _ => "n/a",
    };

    private static string Preview(IReadOnlyList<string> values)
    {
        var sb = new StringBuilder();
        sb.Append(values.Count).Append(" values");
        foreach (var v in values.Take(IssueDraft.MaxRows - 1))
        {
            sb.Append('\n').Append(v);
        }

        return sb.ToString();
    }
}
