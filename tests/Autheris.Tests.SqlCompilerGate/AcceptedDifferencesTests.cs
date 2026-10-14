namespace Autheris.Tests.SqlCompilerGate;

using System.Text.Json;
using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;

/// <summary><c>accepted-differences.json</c> and its schema (plan 16.5 M-6): an <c>ast-looser-*</c> entry can never be accepted.</summary>
public sealed class AcceptedDifferencesTests
{
    private const string Id = "0123456789ab";

    private static string Entry(string cls, string approval = "pending", string extra = "") => $$"""
        {
          "schemaVersion": 1,
          "entries": [
            {
              "dialects": ["PostgreSql"],
              "class": "{{cls}}",
              "reason": "SqlCompileNotSupportedException",
              "statements": ["{{Id}}"],
              "justification": "The construct is rejected by the AST compiler on purpose (fail closed).",
              "approval": "{{approval}}"{{extra}}
            }
          ]
        }
        """;

    [Theory]
    [InlineData("ast-looser-rows")]
    [InlineData("ast-looser-mask")]
    [InlineData("ast-looser-write")]
    [InlineData("ast-looser-accepts")]
    [InlineData("ast-accepts")]
    [InlineData("identical")]
    [InlineData("anything-else")]
    public void AcceptedDifferencesSchema_RejectsAstLooser(string cls)
    {
        var ex = Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(Entry(cls)));

        ex.Errors.ShouldContain(e => e.Contains("not acceptable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ast-stricter")]
    [InlineData("ast-error")]
    public void AcceptedDifferences_AcceptsStricterAndError_OnlyForTheListedStatementDialectAndReason(string cls)
    {
        var accepted = AcceptedDifferences.Parse(Entry(cls));
        var klass = cls == "ast-stricter" ? DifferenceClass.AstStricter : DifferenceClass.AstError;

        accepted.Accepts(Id, GateDialect.PostgreSql, klass, "SqlCompileNotSupportedException").ShouldBeTrue();
        accepted.Accepts("ffffffffffff", GateDialect.PostgreSql, klass, "SqlCompileNotSupportedException").ShouldBeFalse();
        accepted.Accepts(Id, GateDialect.Oracle, klass, "SqlCompileNotSupportedException").ShouldBeFalse();
        accepted.Accepts(Id, GateDialect.PostgreSql, klass, "OtherException").ShouldBeFalse();
        accepted.Accepts(Id, GateDialect.PostgreSql, DifferenceClass.AstLooserRows, "SqlCompileNotSupportedException").ShouldBeFalse();
    }

    [Fact]
    public void AcceptedDifferencesSchema_ApprovedEntriesNeedAnIssueAndAReviewer()
    {
        Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(Entry("ast-stricter", "approved")));

        var accepted = AcceptedDifferences.Parse(Entry("ast-stricter", "approved", ",\n\"issue\": \"https://example.test/issues/1\",\n\"reviewer\": \"@security-lead\""));
        accepted.HasPendingEntries.ShouldBeFalse();
        AcceptedDifferences.Parse(Entry("ast-stricter")).HasPendingEntries.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "dialects": ["Nope"], "class": "ast-error", "reason": "X", "statements": ["0123456789ab"], "justification": "long enough justification text", "approval": "pending" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "dialects": ["Oracle"], "class": "ast-error", "reason": "X", "statements": ["not-an-id"], "justification": "long enough justification text", "approval": "pending" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "dialects": ["Oracle"], "class": "ast-error", "reason": "X", "statements": ["0123456789ab"], "justification": "short", "approval": "pending" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "dialects": ["Oracle"], "class": "ast-error", "reason": "X", "statements": ["0123456789ab"], "justification": "long enough justification text", "approval": "pending", "extra": 1 } ] }""")]
    [InlineData("""{ "schemaVersion": 2, "entries": [] }""")]
    [InlineData("not json")]
    public void AcceptedDifferencesSchema_RejectsMalformedFiles(string json)
    {
        Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(json));
    }

    [Fact]
    public void TheSchemaFile_AllowsExactlyTheClassesTheValidatorAccepts()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.schema.json")));
        var classes = schema.RootElement.GetProperty("properties").GetProperty("entries").GetProperty("items")
            .GetProperty("properties").GetProperty("class").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();

        classes.Order().ShouldBe(AcceptedDifferences.AcceptableClasses.Order());
        classes.ShouldAllBe(c => !c!.StartsWith("ast-looser", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommittedFile_IsSchemaValid_AndNeverListsALooserClass()
    {
        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));

        accepted.Entries.ShouldAllBe(e => AcceptedDifferences.AcceptableClasses.Contains(e.Class));
        var known = CorpusLoader.LoadAll().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        accepted.Entries.SelectMany(e => e.Statements).ShouldAllBe(id => known.Contains(id));
    }

    [Fact]
    public void CutoverPrecondition_EveryAcceptedDifferenceIsApproved()
    {
        // §16.5 (Phase 3 sign-off on the final accepted-differences.json): enforced only in the cutover profile.
        if (!string.Equals(Environment.GetEnvironmentVariable("SQLGATE_PROFILE"), "cutover", StringComparison.OrdinalIgnoreCase)) return;

        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));

        accepted.HasPendingEntries.ShouldBeFalse("accepted-differences.json still has pending entries: the security reviewers have not signed off");
    }

    private static string Defect(string approval = "approved", string reviewer = "stakeholder (2026-10-11)") => $$"""
        {
          "schemaVersion": 1,
          "entries": [],
          "legacyDefects": [
            {
              "dialects": ["PostgreSql"],
              "statements": ["{{Id}}"],
              "defect": "Legacy under-returns the rows of a recursive CTE (8 instead of 24).",
              "justification": "The AST result is the correct one; the legacy baseline is wrong.",
              "reviewer": "{{reviewer}}",
              "approval": "{{approval}}"
            }
          ]
        }
        """;

    [Fact]
    public void LegacyDefect_ApprovedEntry_IsRecognisedPerStatementAndDialect()
    {
        var accepted = AcceptedDifferences.Parse(Defect());

        accepted.IsLegacyDefect(Id, GateDialect.PostgreSql).ShouldBeTrue();
        accepted.IsLegacyDefect(Id, GateDialect.Oracle).ShouldBeFalse();
        accepted.IsLegacyDefect("ba9876543210", GateDialect.PostgreSql).ShouldBeFalse();
    }

    [Fact]
    public void LegacyDefect_PendingEntry_IsRejected() =>
        Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(Defect(approval: "pending")));

    [Fact]
    public void LegacyDefect_AnonymousReviewer_IsRejected() =>
        Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(Defect(reviewer: "someone")));

    [Fact]
    public void LegacyDefect_DoesNotMakeAnAstLooserClassAcceptable() =>
        // the class enum of entries is unchanged: an ast-looser-rows entry is still invalid
        Should.Throw<AcceptedDifferencesSchemaException>(() => AcceptedDifferences.Parse(Entry("ast-looser-rows")));
}
