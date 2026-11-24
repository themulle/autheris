using System.Linq;
using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Step 0043 (<c>single_approval_cleanup</c>) discards access profiles by their top-level status only. A substring match on the
/// definition text would also delete an active profile whose nested draft is pending (a live row restriction would disappear).
/// The executed behaviour on both engines is covered by the integration test <c>SingleApprovalCleanupMigrationStep0043Tests</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class SingleApprovalCleanupStepSqlTests
{
    [Theory]
    [InlineData("postgresql", "definition_json::json ->> 'Status'")]
    [InlineData("sqlserver", "JSON_VALUE(definition_json, N'$.Status')")]
    public void Profile_cleanup_reads_the_top_level_status_only(string engine, string topLevelExtraction)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 43 && s.Name == "single_approval_cleanup");
        var profileDelete = step.Batches
            .SelectMany(b => b.Split(';'))
            .Single(statement => statement.Contains("DELETE FROM", System.StringComparison.Ordinal) &&
                                 statement.Contains("VIRTUAL_FILTER_ACCESS_PROFILES", System.StringComparison.Ordinal));

        profileDelete.ShouldContain(topLevelExtraction);
        profileDelete.ShouldNotContain(" LIKE ", Case.Insensitive);
    }
}
