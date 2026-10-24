using Autheris.Tests.Simulation.Corpus;
using Autheris.Tests.Simulation.Infrastructure;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Pure unit tests of the P1 corpus code. Fixtures are our own <c>Corpus/fixtures/mini.test</c>; no third-party content.</summary>
public sealed class SltCorpusUnitTests
{
    private static string MiniText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "fixtures", "mini.test"));

    [Fact]
    public void SltParser_ParsesStatementAndQueryRecords()
    {
        var records = SltParser.Parse(MiniText, "mini.test");

        records.OfType<SltHashThreshold>().Single().Threshold.ShouldBe(4);
        var statements = records.OfType<SltStatement>().ToList();
        statements.Count.ShouldBe(5);
        statements.Count(s => !s.ExpectOk).ShouldBe(1);
        var queries = records.OfType<SltQuery>().ToList();
        queries.Count.ShouldBe(4);
        queries[1].Types.ShouldBe("I");
        queries[1].Sort.ShouldBe(SltSortMode.RowSort);
        queries[1].Label.ShouldBe("sqlite-label");
        queries[1].Expected.ShouldBeOfType<SltValues>().Values.ShouldBe(["1", "2", "3"]);
        queries[2].Expected.ShouldBeOfType<SltValues>().Values.ShouldBeEmpty();
        queries[3].Expected.ShouldBeOfType<SltHash>().Count.ShouldBe(6);
        queries[0].Conditions.Single().ShouldBe(new SltCondition(true, "sqlite"));
    }

    [Fact]
    public void SltParser_DialectFilterAppliesSkipifAndOnlyif()
    {
        var queries = SltParser.Parse(MiniText, "mini.test").OfType<SltQuery>().ToList();

        SltDialectFilter.Applies(queries[0]).ShouldBeFalse(); // skipif sqlite
        SltDialectFilter.Applies(queries[1]).ShouldBeTrue(); // onlyif sqlite
        SltDialectFilter.Applies(queries[2]).ShouldBeTrue();
        SltDialectFilter.Applies(queries[1], "postgresql").ShouldBeFalse();
    }

    [Theory]
    [InlineData("frobnicate\nSELECT 1\n")]
    [InlineData("include other.test\n")]
    [InlineData("control sortmode valuesort\n")]
    [InlineData("query I nosort\nSELECT 1\n")]
    [InlineData("skipif sqlite\n")]
    public void SltParser_RejectsUnknownDirective(string text) =>
        Should.Throw<SltParseException>(() => SltParser.Parse(text, "x.test"));

    [Fact]
    public void SltParser_IdsStableAcrossRuns()
    {
        var a = SltParser.Parse(MiniText, "mini.test").OfType<SltQuery>().Select(q => q.Id).ToList();
        var b = SltParser.Parse(MiniText, "mini.test").OfType<SltQuery>().Select(q => q.Id).ToList();

        a.ShouldBe(b);
        a.Distinct().Count().ShouldBe(a.Count);
        a.ShouldAllBe(id => id.Length == 12);
        SltParser.Parse(MiniText, "other.test").OfType<SltQuery>().First().Id.ShouldNotBe(a[0]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SltParser_RejectsOversizedFileAndLine()
    {
        Should.Throw<SltParseException>(() => SltParser.Parse(new string('x', SltParser.MaxFileChars + 1), "big.test")).Message.ShouldContain("exceeds");
        Should.Throw<SltParseException>(() => SltParser.Parse("statement ok\nSELECT '" + new string('a', SltParser.MaxLineChars) + "'\n", "wide.test")).Message.ShouldContain("line");

        var many = string.Concat(Enumerable.Repeat("halt\n", SltParser.MaxRecords + 1));
        Should.Throw<SltParseException>(() => SltParser.Parse(many, "many.test")).Message.ShouldContain("records");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SltParser_TreatsContentAsDataOnly()
    {
        var marker = Path.Combine(Path.GetTempPath(), "sim-slt-pwned-" + Guid.NewGuid().ToString("N"));
        var hostile = $"statement ok\nATTACH DATABASE '{marker}' AS x; SELECT load_extension('evil'); $(touch {marker}) `touch {marker}`\n\n"
                      + $"query T rowsort\nSELECT '.shell touch {marker}'\n----\n.shell touch {marker}\n";

        var records = SltParser.Parse(hostile, "hostile.test");

        records.Count.ShouldBe(2);
        records.OfType<SltStatement>().Single().Sql.ShouldContain("load_extension('evil')");
        File.Exists(marker).ShouldBeFalse();
        // content that tries to pull in another file or run a command is an unknown directive, never followed
        Should.Throw<SltParseException>(() => SltParser.Parse($"include {marker}\n", "inc.test"));
        Should.Throw<SltParseException>(() => SltParser.Parse($"!touch {marker}\n", "sh.test"));
        File.Exists(marker).ShouldBeFalse();
    }

    [Fact]
    public void SltFormatter_HashMatchesSltSpecSample()
    {
        // md5 over every value followed by a newline (sqllogictest spec)
        SltResultFormatter.Md5(["1", "2", "3"]).ShouldBe("c0710d6b4f15dfa88f600b0e6b624077");
        SltResultFormatter.Md5(["NULL", "(empty)"]).ShouldBe("14ad0be6c1ebb606277cbbb8e0bcc287");

        var values = SltResultFormatter.Format([[1L], [2L], [3L]], "I", SltSortMode.NoSort);
        SltResultFormatter.Matches(values, new SltHash(3, "c0710d6b4f15dfa88f600b0e6b624077")).ShouldBeTrue();
        SltResultFormatter.Matches(values, new SltHash(2, "c0710d6b4f15dfa88f600b0e6b624077")).ShouldBeFalse();
        SltResultFormatter.Matches(values, new SltValues(["1", "2", "3"])).ShouldBeTrue();
    }

    [Fact]
    public void SltFormatter_AppliesTypesRowsortAndValuesort()
    {
        object?[][] rows = [[3L, "b", 1.5], [1L, "", null], [2L, "aé", 2.0]];

        SltResultFormatter.Format(rows, "ITR", SltSortMode.NoSort).ShouldBe(["3", "b", "1.500", "1", "(empty)", "NULL", "2", "a@", "2.000"]);
        SltResultFormatter.Format(rows, "ITR", SltSortMode.RowSort).ShouldBe(["1", "(empty)", "NULL", "2", "a@", "2.000", "3", "b", "1.500"]);
        SltResultFormatter.Format(rows, "ITR", SltSortMode.ValueSort).ShouldBe(["(empty)", "1", "1.500", "2", "2.000", "3", "NULL", "a@", "b"]);
        SltResultFormatter.Format([[2.9, "12abc"]], "II", SltSortMode.NoSort).ShouldBe(["2", "12"]);
        Should.Throw<InvalidOperationException>(() => SltResultFormatter.Format([[1L]], "II", SltSortMode.NoSort));
    }

    private static List<SltQuery> Queries(int n) =>
        [.. Enumerable.Range(1, n).Select(i => new SltQuery("f.test", i, "I", SltSortMode.NoSort, null, $"SELECT {i}", new SltValues([i.ToString(System.Globalization.CultureInfo.InvariantCulture)])))];

    [Fact]
    public void SltSampler_SameSeedSameSample() =>
        SltSampler.Select(Queries(200), 20261024, 50).Select(q => q.Id).ShouldBe(SltSampler.Select(Queries(200), 20261024, 50).Select(q => q.Id));

    [Fact]
    public void SltSampler_DifferentSeedDifferentSample() =>
        SltSampler.Select(Queries(200), 1, 50).Select(q => q.Id).ShouldNotBe(SltSampler.Select(Queries(200), 2, 50).Select(q => q.Id));

    [Fact]
    public void SltSampler_RespectsSize()
    {
        var all = Queries(200);
        SltSampler.Select(all, 7, 50).Count.ShouldBe(50);
        SltSampler.Select(all, 7, int.MaxValue).Count.ShouldBe(200);
        SltSampler.Select(all, 7, 0).ShouldBeEmpty();
        var picked = SltSampler.Select(all, 7, 50);
        picked.Select(q => q.Line).ShouldBe(picked.Select(q => q.Line).Order());

        SltSampler.ResolveSize(null).ShouldBe(5000);
        SltSampler.ResolveSize("all").ShouldBe(int.MaxValue);
        SltSampler.ResolveSize("12").ShouldBe(12);
        foreach (var bad in new[] { "123456", "-1", "1e3", "ALL", "5 " })
        {
            Should.Throw<InvalidOperationException>(() => SltSampler.ResolveSize(bad), bad);
        }
    }

    private static SltClassifier Classifier(SimKnownGaps gaps, params string[] sameClass) =>
        new(new HashSet<string>(sameClass), new HashSet<string>(sameClass), gaps);

    [Fact]
    [Trait("Category", "Security")]
    public void SltClassifier_PolicyLeakNeverKnownGap()
    {
        const string cls = "PolicyLeak:RowFilter:result of the unfiltered table returned";
        var gaps = new SimKnownGaps();
        Should.Throw<InvalidOperationException>(() => gaps.Register("SIM-GAP-001", FindingKind.PolicyLeak, cls));
        // even when the same class text is registered for another kind and listed as accepted or unsupported, the leak stays a leak
        gaps.Register("SIM-GAP-002", FindingKind.WrongResult, cls);

        Classifier(gaps, cls).Classify(new SltObservation(FindingKind.PolicyLeak, cls)).ShouldBe(SltVerdict.PolicyLeak);
        SltClassifier.IsRed(SltVerdict.PolicyLeak).ShouldBeTrue();
    }

    [Theory]
    [InlineData(FindingKind.Crash, SltVerdict.Crash)]
    [InlineData(FindingKind.ServerError, SltVerdict.Crash)]
    [InlineData(FindingKind.Hang, SltVerdict.Hang)]
    public void SltClassifier_CrashNeverKnownGap(FindingKind kind, SltVerdict expected)
    {
        const string cls = "Crash:Compile/NullReferenceException:x";
        var gaps = new SimKnownGaps();
        Should.Throw<InvalidOperationException>(() => gaps.Register("SIM-GAP-003", kind, cls));

        Classifier(gaps, cls).Classify(new SltObservation(kind, cls)).ShouldBe(expected);
        SltClassifier.IsRed(expected).ShouldBeTrue();
    }

    [Fact]
    public void SltClassifier_MapsEqualAcceptedUnsupportedKnownGapAndFinding()
    {
        var gaps = new SimKnownGaps();
        gaps.Register("SIM-GAP-004", FindingKind.WrongResult, "WrongResult:gap");
        var classifier = new SltClassifier(new HashSet<string> { "WrongResult:accepted" }, new HashSet<string> { "UnexpectedRejection:unsupported" }, gaps);

        classifier.Classify(new SltObservation(null, string.Empty)).ShouldBe(SltVerdict.Equal);
        classifier.Classify(new SltObservation(null, string.Empty, HarnessMismatch: true)).ShouldBe(SltVerdict.HarnessMismatch);
        classifier.Classify(new SltObservation(FindingKind.WrongResult, "WrongResult:accepted")).ShouldBe(SltVerdict.AcceptedDifference);
        classifier.Classify(new SltObservation(FindingKind.UnexpectedRejection, "UnexpectedRejection:unsupported")).ShouldBe(SltVerdict.UnsupportedByDesign);
        classifier.Classify(new SltObservation(FindingKind.WrongResult, "WrongResult:gap")).ShouldBe(SltVerdict.KnownGap);
        classifier.Classify(new SltObservation(FindingKind.WrongResult, "WrongResult:new")).ShouldBe(SltVerdict.Finding);
        // an unsupported entry only excuses rejections, an accepted difference only wrong results
        classifier.Classify(new SltObservation(FindingKind.WrongResult, "UnexpectedRejection:unsupported")).ShouldBe(SltVerdict.Finding);
    }

    [Fact]
    public void SltClassifier_ClassFilesAreFailClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sim-cls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Write(string json)
            {
                var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, json);
                return path;
            }

            SltClassifier.LoadClassFile(Write("""{"entries":[{"errorClass":"a","reason":"r"}]}"""), "reason").ShouldBe(["a"]);
            Should.Throw<InvalidOperationException>(() => SltClassifier.LoadClassFile(Write("""{"entries":[{"errorClass":"a","reason":"r","x":1}]}"""), "reason"));
            Should.Throw<InvalidOperationException>(() => SltClassifier.LoadClassFile(Write("""{"entries":[{"errorClass":"a"}]}"""), "reason"));
            Should.Throw<InvalidOperationException>(() => SltClassifier.LoadClassFile(Write("""{"entries":[{"errorClass":"a","reason":"r"},{"errorClass":"a","reason":"r"}]}"""), "reason"));
            Should.Throw<InvalidOperationException>(() => SltClassifier.LoadClassFile(Write("[]"), "reason"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SltReference_DisablesExtensionLoadingAndOutsideAttach()
    {
        await using var reference = SltReference.Create();
        await reference.ExecuteAsync("CREATE TABLE t(a INTEGER)");

        // the guard refuses the constructs before SQLite sees them
        foreach (var sql in new[]
                 {
                     "SELECT load_extension('/lib/evil.so')",
                     "ATTACH DATABASE '/tmp/outside.db' AS o",
                     "attach database '/etc/passwd' as p",
                     "DETACH DATABASE o",
                     "PRAGMA writable_schema = ON",
                     "VACUUM INTO '/tmp/leak.db'",
                 })
        {
            await Should.ThrowAsync<SltReferenceRejectedException>(async () => await reference.RunAsync(sql), sql);
            await Should.ThrowAsync<SltReferenceRejectedException>(async () => await reference.ExecuteAsync(sql), sql);
        }

        // and SQLite itself refuses extension loading on every connection the harness opens
        using var connection = SltReference.OpenConnection(reference.Path, readOnly: false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT load_extension('/nonexistent/evil')";
        Should.Throw<SqliteException>(() => cmd.ExecuteScalar());

        var outside = Path.Combine(Path.GetTempPath(), "sim-outside-" + Guid.NewGuid().ToString("N") + ".db");
        await Should.ThrowAsync<SltReferenceRejectedException>(async () => await reference.ExecuteAsync($"ATTACH DATABASE '{outside}' AS o"));
        File.Exists(outside).ShouldBeFalse();
    }

    [Fact]
    public async Task SltReference_CloneCopiesDataAndKeepsTheOriginalIntact()
    {
        await using var reference = SltReference.Create();
        await reference.ExecuteAsync("CREATE TABLE t(a INTEGER)");
        await reference.ExecuteAsync("INSERT INTO t VALUES(1)");
        await using var copy = reference.Clone();
        await copy.ExecuteAsync("DELETE FROM t");

        (await copy.RunAsync("SELECT count(*) FROM t"))[0][0].ShouldBe(0L);
        (await reference.RunAsync("SELECT count(*) FROM t"))[0][0].ShouldBe(1L);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SltEngine_RowFilterProbesNeverReturnRowsOutsideTheFilter()
    {
        var (violations, answered) = await SltRowFilterProbes.RunAsync();

        violations.ShouldBeEmpty();
        answered.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SltCorpusRunner_RunsOwnMiniFixtureThroughBothScenarios()
    {
        // keyed like a row-filter file so scenario B runs as well; the last query carries a bogus hash, so it is a harness mismatch
        var records = SltParser.Parse(MiniText, "test/select1.test");
        var corpus = new Dictionary<string, IReadOnlyList<SltRecord>> { ["test/select1.test"] = records };
        var classifier = new SltClassifier(new HashSet<string>(), new HashSet<string>(), new SimKnownGaps());

        var summary = await SltCorpusRunner.RunAsync(corpus, 1, int.MaxValue, classifier, "repro");

        summary.SampledQueries.ShouldBe(3); // the skipif sqlite query is filtered out
        summary.HarnessMismatches.ShouldBe(1);
        summary.Executed[SltScenario.Permissive].ShouldBe(2);
        summary.Executed[SltScenario.RowFilter].ShouldBe(2);
        summary.Count(SltVerdict.PolicyLeak).ShouldBe(0);
        summary.Count(SltVerdict.Crash).ShouldBe(0);
    }

    [Fact]
    public void SltManifest_ParsesTheCommittedManifestAndRejectsBadFields()
    {
        var manifest = SltCorpus.LoadManifest(SltCorpus.ManifestPath);

        manifest.Commit.Length.ShouldBe(40);
        manifest.TotalQueries.ShouldBeGreaterThanOrEqualTo(5000);
        manifest.Host.ShouldBe("raw.githubusercontent.com");
        Should.Throw<InvalidOperationException>(() => SltCorpus.ParseManifest("""{"commit":"abc","host":"h","repo":"r","files":[]}"""));
        var bad = new string('a', 40);
        Should.Throw<InvalidOperationException>(() => SltCorpus.ParseManifest($$"""{"commit":"{{bad}}","host":"h","repo":"r","files":[{"path":"../x","queries":1,"sha256":"{{new string('b', 64)}}"}]}"""));
        Should.Throw<InvalidOperationException>(() => SltCorpus.ParseManifest($$"""{"commit":"{{bad}}","host":"h","repo":"r","files":[{"path":"a/b.test","queries":1,"sha256":"zz"}]}"""));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SltCorpus_LoadFailsClosedOnMissingDirectoryAndHashMismatch()
    {
        var manifest = SltCorpus.LoadManifest(SltCorpus.ManifestPath);
        Should.Throw<InvalidOperationException>(() => SltCorpus.Load(manifest, Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")))).Message.ShouldContain("fetch-sqllogictest.sh");

        var dir = Path.Combine(Path.GetTempPath(), "sim-slt-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        try
        {
            foreach (var f in manifest.Files)
            {
                File.WriteAllText(Path.Combine(dir, f.Path), "statement ok\nSELECT 1\n");
            }

            Should.Throw<InvalidOperationException>(() => SltCorpus.Load(manifest, dir)).Message.ShouldContain("sha256");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
