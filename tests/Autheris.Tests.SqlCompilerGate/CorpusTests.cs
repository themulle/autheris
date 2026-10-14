namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Harness;
using Shouldly;

/// <summary>The seed corpus (plan 9.2): at least 1,000 read-only and 200 DML statements, extracted from the existing tests.</summary>
public sealed class CorpusTests
{
    [Fact]
    public void SeedCorpus_HasAtLeast1000ReadOnlyAnd200DmlStatements()
    {
        var all = CorpusLoader.LoadAll();

        all.Count(s => !s.IsDml).ShouldBeGreaterThanOrEqualTo(1000);
        all.Count(s => s.IsDml).ShouldBeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public void SeedCorpus_HasUniqueIds_AndWellFormedMetadata()
    {
        var all = CorpusLoader.LoadAll();

        all.Select(s => s.Id).Distinct().Count().ShouldBe(all.Count);
        all.ShouldAllBe(s => s.Sql.Length > 0);
        all.ShouldAllBe(s => s.Expected == "accept" || s.Expected == "reject");
        all.Where(s => !s.IsDml).ShouldAllBe(s => s.Class == "select");
        all.Where(s => s.IsDml).ShouldAllBe(s => s.Class == "insert" || s.Class == "update" || s.Class == "delete" || s.Class == "merge");
    }

    [Fact]
    public void SeedCorpus_ComesFromTheExistingTests_AndHasExecutableFixtureStatements()
    {
        var all = CorpusLoader.LoadAll();

        all.Count(s => s.Source.StartsWith("tests/", StringComparison.Ordinal)).ShouldBeGreaterThan(500);
        all.Count(s => s.Source.StartsWith("generated:", StringComparison.Ordinal)).ShouldBeGreaterThan(100);
        all.ShouldNotContain(s => s.Source.Contains("SqlCompilerGate", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_ReadsMultiLineStatementsAndHeaders()
    {
        var parsed = CorpusLoader.Parse("-- @id 0123456789ab class=select expected=accept source=a.cs\nSELECT 1\nFROM t\n\n-- @id 0123456789ac class=delete expected=reject source=b.cs\nDELETE FROM t\n");

        parsed.Count.ShouldBe(2);
        parsed[0].Sql.ShouldBe("SELECT 1\nFROM t");
        parsed[1].Class.ShouldBe("delete");
        parsed[1].Expected.ShouldBe("reject");
    }
}
