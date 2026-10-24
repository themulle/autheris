using Autheris.Tests.Simulation.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>
/// Guarded corpus tests (run only with <c>AUTHERIS_SIM_RUN=1</c>, by the <c>sim-corpus</c> job). The corpus directory comes from
/// <c>AUTHERIS_SIM_CORPUS_DIR</c>; the files are verified against the manifest sha256 before they are parsed.
/// </summary>
public sealed class SltCorpusTests : SimulationTestBase
{
    private const string Suite = "corpus";
    private const string DraftDirEnvVar = "AUTHERIS_SIM_DRAFT_DIR";

    private static string CorpusClassFile(string name) => Path.Combine(AppContext.BaseDirectory, "Corpus", name);

    private static SltClassifier Classifier(SimKnownGaps gaps) => new(
        SltClassifier.LoadClassFile(CorpusClassFile("slt-accepted-differences.json"), "reason"),
        SltClassifier.LoadClassFile(CorpusClassFile("slt-unsupported.json"), "docRef"),
        gaps);

    [Fact]
    public void Corpus_ManifestProvidesAtLeast5000Queries()
    {
        var manifest = SltCorpus.LoadManifest(SltCorpus.ManifestPath);
        manifest.TotalQueries.ShouldBeGreaterThanOrEqualTo(5000);

        var corpus = SltCorpus.Load(manifest);
        foreach (var file in manifest.Files)
        {
            corpus[file.Path].OfType<SltQuery>().Count().ShouldBe(file.Queries, file.Path);
        }
    }

    [Fact]
    public async Task Corpus_ReferenceAgreesWithRecordedResults()
    {
        var corpus = SltCorpus.Load(SltCorpus.LoadManifest(SltCorpus.ManifestPath));
        var summary = await SltCorpusRunner.RunAsync(corpus, SimSeed.Current, int.MaxValue, Classifier(SltKnownGaps.Create()), "reference-only", referenceOnly: true);

        var total = summary.ReferenceAgreed + summary.HarnessMismatches;
        total.ShouldBeGreaterThan(0);
        (summary.HarnessMismatches * 100.0 / total).ShouldBeLessThanOrEqualTo(1.0, $"{summary.HarnessMismatches} of {total} reference results disagree with the recorded results");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Corpus_RowFilterScenario_NeverLeaksFilteredRows()
    {
        var (violations, answered) = await SltRowFilterProbes.RunAsync();
        violations.ShouldBeEmpty();
        answered.ShouldBeGreaterThan(0, "every probe was rejected, so the row filter was never exercised");

        // the sampled corpus queries of scenario B (select1-3) must not leak either
        var gaps = SltKnownGaps.Create();
        var summary = await RunSampleAsync(gaps, cap: 1000);
        summary.Verdicts.Where(p => p.Key.Scenario == SltScenario.RowFilter && p.Key.Verdict == SltVerdict.PolicyLeak).Sum(p => p.Value).ShouldBe(0);
        summary.Executed.GetValueOrDefault(SltScenario.RowFilter).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Corpus_SampledQueries_MatchReferenceOrKnownGap()
    {
        var gaps = SltKnownGaps.Create();
        var summary = await RunSampleAsync(gaps);

        var reportDir = Environment.GetEnvironmentVariable(SimResults.ReportDirEnvVar) is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "sim-corpus-report");
        summary.Results.Write(reportDir, Suite);
        WriteDrafts(summary);

        summary.TotalExecuted.ShouldBeGreaterThan(0, "no sampled query was executed");
        var sampled = summary.SampledQueries;
        (summary.HarnessMismatches * 100.0 / Math.Max(1, sampled)).ShouldBeLessThanOrEqualTo(1.0, "reference results disagree with the recorded results");

        var red = summary.FirstByClass
            .Where(p => SltClassifier.IsRed(p.Value.Verdict))
            .Select(p => $"{p.Value.Verdict}: {p.Key} (first {p.Value.File}:{p.Value.Line})")
            .ToList();
        red.ShouldBeEmpty(string.Join('\n', red));

        if (SltSampler.ResolveSize(Environment.GetEnvironmentVariable(SltSampler.EnvVar)) == int.MaxValue)
        {
            gaps.EnsureNoStale(summary.FirstByClass.Keys);
        }
    }

    private static async Task<SltRunSummary> RunSampleAsync(SimKnownGaps gaps, int cap = int.MaxValue)
    {
        var corpus = SltCorpus.Load(SltCorpus.LoadManifest(SltCorpus.ManifestPath));
        var size = Math.Min(cap, SltSampler.ResolveSize(Environment.GetEnvironmentVariable(SltSampler.EnvVar)));
        var repro = $"AUTHERIS_SIM_RUN=1 AUTHERIS_SIM_CORPUS_DIR=<dir> AUTHERIS_SIM_SEED={SimSeed.Current} AUTHERIS_SIM_SAMPLE={(size == int.MaxValue ? "all" : size.ToString(System.Globalization.CultureInfo.InvariantCulture))} dotnet test tests/Autheris.Tests.Simulation --filter FullyQualifiedName~SltCorpusTests";
        return await SltCorpusRunner.RunAsync(corpus, SimSeed.Current, size, Classifier(gaps), repro);
    }

    /// <summary>With <c>AUTHERIS_SIM_DRAFT_DIR</c> set, writes one draft per unclassified or known error class (maintainer aid; drafts are committed by hand).</summary>
    private static void WriteDrafts(SltRunSummary summary)
    {
        if (Environment.GetEnvironmentVariable(DraftDirEnvVar) is not { Length: > 0 } dir)
        {
            return;
        }

        foreach (var (_, entry) in summary.FirstByClass.Where(p => p.Value.Verdict is SltVerdict.Finding or SltVerdict.KnownGap))
        {
            IssueDraft.Write(dir, entry.Finding);
        }
    }
}
