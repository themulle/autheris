using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

public sealed class PolicyEpochsChangeVersionMigration0015Tests
{
    [Fact]
    public void Migration_0015_Passes_StepSqlLint_And_Matches_ShapeJson()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 15 && s.Name == "policy_epochs_change_version");
            step.Phase3Marker.ShouldBe("MIG-0015");

            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var repoRoot = FindRepoRoot();
            var checksumsPath = Path.Combine(
                repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var recordedLine = File.ReadAllLines(checksumsPath)
                .Single(l => l.StartsWith("0015 policy_epochs_change_version ", StringComparison.Ordinal));
            var recordedSha = recordedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(recordedSha);

            var shape = GovernanceSchemaShapeLoader.LoadAt(
                engine,
                15,
                steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

            var policyEpochs = shape.Tables.Single(t =>
                string.Equals(t.Name, "POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase));
            var changeVersionCol = policyEpochs.Columns.Single(c =>
                string.Equals(c.Name, "change_version", StringComparison.OrdinalIgnoreCase));
            changeVersionCol.Type.ShouldBe("BIGINT");
            changeVersionCol.Nullable.ShouldBeFalse();

            var expectedIndexName = engine == "postgresql"
                ? "ix_policy_epochs_change_version"
                : "IX_POLICY_EPOCHS_CHANGE_VERSION";
            var idx = policyEpochs.Indexes.Single(i =>
                string.Equals(i.Name, expectedIndexName, StringComparison.OrdinalIgnoreCase));
            idx.Unique.ShouldBeFalse();
            idx.Columns.ShouldBe(["change_version"]);
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Migration_0015_Backfills_ChangeVersion_NeverZeroOrNull()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine)
                .Single(s => s.Version == 15 && s.Name == "policy_epochs_change_version");

            step.Sql.ShouldContain("change_version BIGINT NOT NULL", Case.Insensitive);
            step.Sql.ShouldContain("COALESCE((SELECT structure_version + content_version FROM", Case.Insensitive);
            step.Sql.ShouldContain("WHERE id = 1), 1)", Case.Insensitive);
            step.Sql.ShouldContain("WHERE change_version <= 0", Case.Insensitive);
            step.Sql.ShouldNotContain("SET change_version = NULL", Case.Insensitive);
            Regex.IsMatch(step.Sql, @"SET\s+change_version\s*=\s*0\b", RegexOptions.IgnoreCase).ShouldBeFalse();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root containing Autheris.sln could not be located.");
    }
}
