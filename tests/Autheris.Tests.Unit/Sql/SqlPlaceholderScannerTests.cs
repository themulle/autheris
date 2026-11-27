namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

public sealed class SqlPlaceholderScannerTests
{
    private static readonly Dictionary<string, object?> AllNames = new()
    {
        ["a"] = "v",
        ["b"] = "v",
        ["_x"] = "v"
    };

    // input, the text the guardrail analyses (every placeholder span replaced by ?)
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("'@a'", "'@a'")]
    [InlineData("\"@a\"", "\"@a\"")]
    [InlineData("`@a`", "`@a`")]
    [InlineData("'it''s @a'", "'it''s @a'")]
    [InlineData("@@a", "@@a")]
    [InlineData("x@a", "x@a")]
    [InlineData("@1a", "@1a")]
    [InlineData("-- '\n@a", "-- '\n@a")]
    [InlineData("/* ' */ @a", "/* ' */ @a")]
    [InlineData("$$@a$$", "$$?$$")]
    [InlineData("é@a", "é?")]
    [InlineData("@a@b", "?@b")]
    [InlineData("@_x", "?")]
    [InlineData("x = @", "x = @")]
    [InlineData("x = @a AND y IN (@b)", "x = ? AND y IN (?)")]
    public void GuardrailAndExecutor_ReplaceIdenticalSpans_ForAdversarialInputs(string sql, string expectedNeutralized)
    {
        var neutralized = McpSqlPlaceholders.NeutralizeForAnalysis(sql);
        var executor = GovernedSqlExecutionService.NormalizeClientParameters(sql, AllNames, null, out _);

        // the executor writes the bind-slot name where the guardrail writes ?; the same spans must be replaced
        var executorAsShape = Regex.Replace(executor, "__param_[A-Za-z0-9_]+", "?");

        neutralized.ShouldBe(expectedNeutralized);
        executorAsShape.ShouldBe(neutralized);
    }

    [Fact]
    public void Executor_LeavesPlaceholdersOfUnsuppliedNamesUntouched()
    {
        var text = GovernedSqlExecutionService.NormalizeClientParameters("x = @a AND y = @other", AllNames, null, out var names);

        text.ShouldBe("x = __param_a AND y = @other");
        names.ShouldBe(["a"]);
    }
}
