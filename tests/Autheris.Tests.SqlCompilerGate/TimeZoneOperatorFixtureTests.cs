namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;

/// <summary>ST-130-12 (REQ-SQL-130): corpus fixture <c>80d5942dea95</c> (the infix <c>AT TIME ZONE</c>) is unchanged and still refused by the builder.</summary>
public sealed class TimeZoneOperatorFixtureTests
{
    private const string FixtureId = "80d5942dea95";

    [Fact]
    [Trait("Category", "Security")]
    public void ST_130_12_TheAtTimeZoneFixtureIsUnchangedAndStillRejectedByTheBuilder()
    {
        var fixture = CorpusLoader.LoadAll().Single(s => s.Id == FixtureId);
        fixture.Sql.ShouldBe("SELECT TIMESTAMP '2012-10-31 01:00 UTC' AT TIME ZONE 'America/Los_Angeles'");

        var (tree, _) = new FastSqlEngine().Parse(fixture.Sql.AsMemory());
        Should.Throw<AstBuildException>(() => new SqlAstBuilder().BuildStatement(tree));

        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));
        accepted.Entries.Single(e => e.Statements.Contains(FixtureId)).Reason.ShouldBe("AstBuildException");
    }
}
