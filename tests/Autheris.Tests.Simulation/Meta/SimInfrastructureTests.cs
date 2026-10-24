using System.Text.Json;
using Autheris.Tests.Simulation.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Pure unit tests of the simulation infrastructure; no run guard needed (they live in the Meta namespace).</summary>
public sealed class SimInfrastructureTests
{
    private sealed class GuardedProbe : SimulationTestBase;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("1 ")]
    [Trait("Category", "Security")]
    public void SimRunGuard_FailsClosedWhenEnvNotOne(string? value) =>
        Should.Throw<InvalidOperationException>(() => SimRunGuard.EnsureEnabled(value)).Message.ShouldBe(SimRunGuard.Message);

    [Fact]
    public void SimRunGuard_AllowsExactlyOne() => Should.NotThrow(() => SimRunGuard.EnsureEnabled("1"));

    [Fact]
    public void SimulationTestBase_ConstructorEnforcesGuardWhenEnvUnset()
    {
        if (Environment.GetEnvironmentVariable(SimRunGuard.EnvVar) == "1")
        {
            new GuardedProbe().ShouldNotBeNull();
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => new GuardedProbe());
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345678901")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 7")]
    [Trait("Category", "Security")]
    public void SimSeed_RejectsMalformedValue(string value) =>
        Should.Throw<InvalidOperationException>(() => SimSeed.Resolve(value, DateTime.UtcNow));

    [Fact]
    public void SimSeed_DefaultsToUtcDateAndParsesDigits()
    {
        SimSeed.Resolve(null, new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc)).ShouldBe(20261024);
        SimSeed.Resolve("", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)).ShouldBe(20260102);
        SimSeed.Resolve("4242", DateTime.UtcNow).ShouldBe(4242);
        SimSeed.Resolve("4294967295", DateTime.UtcNow).ShouldBe(4294967295);
    }

    [Fact]
    public void SimSeed_HashIsStableAndSeedDependent()
    {
        SimSeed.Hash(1, "a").ShouldBe(SimSeed.Hash(1, "a"));
        SimSeed.Hash(1, "a").ShouldNotBe(SimSeed.Hash(2, "a"));
        SimSeed.Hash(1, "a").ShouldNotBe(SimSeed.Hash(1, "b"));
    }

    [Fact]
    public void SimSeed_DescribeNamesTheSeed() => SimSeed.Describe().ShouldStartWith("seed=");

    [Fact]
    public void ErrorClass_NormalisesDigitsQuotesAndLength()
    {
        SimErrorClass.Build(FindingKind.WrongResult, "500", "row 42 of 'abc' and \"x y\"").ShouldBe("WrongResult:500:row # of '?' and '?'");
        SimErrorClass.Build(FindingKind.Crash, "System.X", new string('a', 400)).Length.ShouldBe(120);
        SimErrorClass.Build(FindingKind.Crash, "E", null).ShouldBe("Crash:E:");
    }

    [Theory]
    [InlineData(FindingKind.Crash)]
    [InlineData(FindingKind.Hang)]
    [InlineData(FindingKind.ServerError)]
    [InlineData(FindingKind.PolicyLeak)]
    [Trait("Category", "Security")]
    public void SimKnownGaps_RejectSecurityKinds(FindingKind kind) =>
        Should.Throw<InvalidOperationException>(() => new SimKnownGaps().Register("SIM-GAP-001", kind, "x"));

    [Fact]
    public void SimKnownGaps_AcceptsFunctionalKindsAndMatchesByClassAndKind()
    {
        var gaps = new SimKnownGaps();
        gaps.Register("SIM-GAP-001", FindingKind.WrongResult, "WrongResult:#:x");
        gaps.IsKnown(new SimFinding(FindingKind.WrongResult, "WrongResult:#:x", "q", "e", "a", "r")).ShouldBeTrue();
        gaps.IsKnown(new SimFinding(FindingKind.UnexpectedRejection, "WrongResult:#:x", "q", "e", "a", "r")).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => gaps.Register("bad", FindingKind.WrongResult, "y"));
        Should.Throw<InvalidOperationException>(() => gaps.Register("SIM-GAP-002", FindingKind.WrongResult, "WrongResult:#:x"));
    }

    [Fact]
    public void SimKnownGaps_StrictXfailFailsForResolvedGap()
    {
        var gaps = new SimKnownGaps();
        gaps.Register("SIM-GAP-007", FindingKind.WrongResult, "c1");
        gaps.Register("SIM-GAP-008", FindingKind.UnexpectedRejection, "c2");
        gaps.EnsureNoStale(["c1", "c2", "other"]);
        Should.Throw<InvalidOperationException>(() => gaps.EnsureNoStale(["c1"])).Message.ShouldBe("remove KnownGap SIM-GAP-008");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IssueDraft_RedactsCanariesAndCapsCells()
    {
        var rows = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"row{i} SIMCANARY-MASK-7f3a91 " + new string('x', 300)));
        var finding = new SimFinding(FindingKind.WrongResult, "WrongResult:#:x", "select SIMCANARY-TENANT-9b1c42", "X-Test-Roles: Admin\nAuthorization: Bearer t\nok", rows, "run SIMCANARY-A");
        var text = IssueDraft.Render(finding);

        text.ShouldNotContain("SIMCANARY");
        text.ShouldContain("<canary>");
        text.ShouldNotContain("X-Test-Roles");
        text.ShouldNotContain("Authorization");
        text.Split('\n').Max(l => l.Length).ShouldBeLessThanOrEqualTo(IssueDraft.MaxCellChars + 10);
        text.ShouldNotContain("row20");
        text.ShouldContain("row19");
    }

    [Fact]
    public void IssueDraft_WritesIdFileAndRejectsUnsafeIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sim-draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            var finding = new SimFinding(FindingKind.WrongResult, "WrongResult:#:x", "q", "e", "a", "r");
            var path = IssueDraft.Write(dir, finding, "SIM-GAP-001");
            Path.GetFileName(path).ShouldBe("SIM-GAP-001.md");
            File.Exists(path).ShouldBeTrue();
            Should.Throw<ArgumentException>(() => IssueDraft.Write(dir, finding, "../evil"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SimResults_WritesOneJsonlLinePerErrorClassWithCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sim-res-" + Guid.NewGuid().ToString("N"));
        try
        {
            var results = new SimResults();
            var f = new SimFinding(FindingKind.WrongResult, "WrongResult:#:x", "q", "e", "a", "repro SIMCANARY-Z");
            results.Add(f);
            results.Add(f);
            results.Add(f with { ErrorClass = "WrongResult:#:y" });
            var path = results.Write(dir, "corpus");

            Path.GetFileName(path).ShouldBe("corpus-results.jsonl");
            var lines = File.ReadAllLines(path);
            lines.Length.ShouldBe(2);
            using var doc = JsonDocument.Parse(lines[0]);
            doc.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
            doc.RootElement.GetProperty("v").GetInt32().ShouldBe(SimResults.SchemaVersion);
            File.ReadAllText(path).ShouldNotContain("SIMCANARY");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Sha(char c) => new(c, 64);

    [Fact]
    [Trait("Category", "Security")]
    public void SimQuarantine_RequiresIssueAndExpires()
    {
        var ok = $$"""[{"sha256":"{{Sha('a')}}","issue":12,"reason":"r","approvedOn":"2026-10-01"}]""";
        var entries = SimQuarantine.Parse(ok);
        entries.Count.ShouldBe(1);
        SimQuarantine.IsQuarantined(entries, Sha('a')).ShouldBeTrue();
        SimQuarantine.IsQuarantined(entries, Sha('b')).ShouldBeFalse();
        SimQuarantine.Parse("[]").ShouldBeEmpty();

        SimQuarantine.Expired(entries, new DateOnly(2026, 12, 30)).ShouldBeEmpty(); // day 90
        SimQuarantine.Expired(entries, new DateOnly(2026, 12, 31)).Count.ShouldBe(1); // day 91
        SimQuarantine.Expired(entries, new DateOnly(2026, 9, 30)).Count.ShouldBe(1); // dated in the future

        string[] bad =
        [
            $$"""[{"sha256":"{{Sha('a')}}","reason":"r","approvedOn":"2026-10-01"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":0,"reason":"r","approvedOn":"2026-10-01"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":"7","reason":"r","approvedOn":"2026-10-01"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":1,"reason":"r"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":1,"reason":"r","approvedOn":"01.10.2026"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":1,"reason":" ","approvedOn":"2026-10-01"}]""",
            """[{"sha256":"ABC","issue":1,"reason":"r","approvedOn":"2026-10-01"}]""",
            $$"""[{"sha256":"{{Sha('a')}}","issue":1,"reason":"r","approvedOn":"2026-10-01","extra":1}]""",
            "{}",
            "[1]",
        ];
        foreach (var json in bad)
        {
            Should.Throw<InvalidOperationException>(() => SimQuarantine.Parse(json), json);
        }
    }
}
