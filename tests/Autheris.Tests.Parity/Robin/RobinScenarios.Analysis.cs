using System.Text.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Robin;

// Issue 1204: query analysis agrees with execution. Part of the same class: no own [Trait]/[Collection] (inherited from the attributed part).
public sealed partial class RobinScenarios
{
    /// <summary>Issue 1204: the analysis of an ad-hoc statement carries the code the SQL endpoint answers for the same statement.</summary>
    [ParityTheory]
    [InlineData("SELECT id FROM parity_sales /* a /* b */ c */", "SQL_COMMENT_NOT_PERMITTED")]
    [InlineData("SELECT NOPE_FN(amount) AS t FROM parity_sales", "SQL_FUNCTION_NOT_PERMITTED")]
    [InlineData("SELECT id FROM parity_sales TABLESAMPLE BERNOULLI (10)", "SQL_CONSTRUCT_NOT_SUPPORTED")]
    [InlineData("SELECT MAX(amount) AS m FROM parity_masked", "AGGREGATION_ON_MASKED_COLUMN")]
    [InlineData("SELECT id FROM parity_masked WHERE amount > 100", "FILTER_ON_MASKED_COLUMN")]
    public Task Robin_1204_AnalyzeAgreesWithExecution(string sql, string code) => Run("WebSql", "analyze_agrees_with_execution", async () =>
    {
        var executed = await Sql(sql);
        executed.Rejected(code);

        var r = await Analyze(sql);
        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        (FindProperty(doc.RootElement, "isValid") ?? throw new XunitException("isValid missing")).ValueKind.ShouldBe(JsonValueKind.False);
        ContainsString(doc.RootElement, code).ShouldBeTrue($"the analysis does not report {code}, the code execution answers");
        NoRowValuesOrStackFrames(r.Body);
    }, code);
}
