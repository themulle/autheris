namespace Autheris.Tests.Unit.Governance;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Shouldly;
using Xunit;

/// <summary>The analysis audit event stores counts and codes only, and is the only event a compile rejection writes.</summary>
[Trait("Category", "Security")]
public sealed class QueryAnalysisAuditTests
{
    [Fact]
    public async Task AnalysisAudit_ContainsNoCompileNames()
    {
        var h = new SqlCompileParityHarness(consents: SqlCompileParityHarness.AllowConsent());

        await h.AnalyzeAsync("SELECT pg_sleep(1) FROM demo.main.t TABLESAMPLE BERNOULLI (10)", SqlCompileParityHarness.Caller());

        var entry = h.Audit.Entries.ShouldHaveSingleItem();
        var text = entry.Query + entry.DetailsJson + entry.TargetTable;
        text.ShouldNotContain("pg_sleep");
        text.ShouldNotContain("TABLESAMPLE", Case.Insensitive);
    }

    [Fact]
    public async Task OnlyQueryAnalysisEvaluatedEventWritten()
    {
        var h = new SqlCompileParityHarness(consents: SqlCompileParityHarness.AllowConsent());

        await h.AnalyzeAsync("SELECT pg_sleep(1) FROM demo.main.t", SqlCompileParityHarness.Caller());

        h.Audit.Entries.Select(e => e.EventType).ShouldBe([AuditEventTypes.QueryAnalysisEvaluated]);
    }
}
