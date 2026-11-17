using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class PolicyEpochsMigrationStep0015Tests
{
    [Fact]
    public void Migration_0015_Backfills_Existing_PolicyEpochs_Rows_To_CatalogState_Sum()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql")
            .Single(s => s.Version == 16 && s.Name == "policy_epochs_change_version");

        step.Sql.ShouldContain("change_version");
        step.Sql.ShouldContain("structure_version + content_version");
    }
}
