using System.Text.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Robin;

// Issue 1204 helper, called from Robin_79_AnalyzeInvalidSqlStableCode (the suite holds exactly 100 scenario methods). Same class: no own [Trait]/[Collection].
public sealed partial class RobinScenarios
{
    /// <summary>For every statement the SQL endpoint rejects with a stable code, the analysis reports that same code.</summary>
    private async Task AnalysisAgreesWithExecution()
    {
        foreach (var (sql, code) in new[]
        {
            ("SELECT id FROM parity_sales /* a /* b */ c */", "SQL_COMMENT_NOT_PERMITTED"),
            ("SELECT NOPE_FN(amount) AS t FROM parity_sales", "SQL_FUNCTION_NOT_PERMITTED"),
            ("SELECT id FROM parity_sales TABLESAMPLE BERNOULLI (10)", "SQL_CONSTRUCT_NOT_SUPPORTED"),
            ("SELECT MAX(amount) AS m FROM parity_masked", "AGGREGATION_ON_MASKED_COLUMN"),
            ("SELECT id FROM parity_masked WHERE amount > 100", "FILTER_ON_MASKED_COLUMN"),
        })
        {
            (await Sql(sql)).Rejected(code);
            AssertAnalysisReportsInvalid(await Analyze(sql), code);
        }
    }

    private static void AssertAnalysisReportsInvalid(RobinHttpResponse analysed, string code)
    {
        analysed.Result.Ok();
        using var doc = JsonDocument.Parse(analysed.Body);
        (FindProperty(doc.RootElement, "isValid") ?? throw new XunitException("isValid missing")).ValueKind.ShouldBe(JsonValueKind.False);
        ContainsString(doc.RootElement, code).ShouldBeTrue($"the analysis does not report {code}, the code execution answers");
        NoRowValuesOrStackFrames(analysed.Body);
    }
}
