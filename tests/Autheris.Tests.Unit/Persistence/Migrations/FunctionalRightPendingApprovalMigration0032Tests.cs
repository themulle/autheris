using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class FunctionalRightPendingApprovalMigration0032Tests
{
    [Fact]
    public void Step0032_PostgreSql_DropsNotNullOnApprovalColumns()
    {
        var step = Step("postgresql");

        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
        AssertMatchesGoldenChecksum("postgresql", step);
        step.Batches.ShouldHaveSingleItem();
        step.Sql.ShouldContain("ALTER TABLE FUNCTIONAL_RIGHT_MAPPINGS", Case.Insensitive);
        step.Sql.ShouldContain("ALTER COLUMN approved_by DROP NOT NULL", Case.Insensitive);
        step.Sql.ShouldContain("ALTER COLUMN approved_at DROP NOT NULL", Case.Insensitive);
    }

    [Fact]
    public void Step0032_SqlServer_RestatesTypesAsNullable()
    {
        var step = Step("sqlserver");

        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
        AssertMatchesGoldenChecksum("sqlserver", step);
        step.Batches.Count.ShouldBe(2);
        step.Batches[0].ShouldContain("COLUMNPROPERTY", Case.Insensitive);
        step.Batches[0].ShouldContain("ALTER TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_by NVARCHAR(128) NULL", Case.Insensitive);
        step.Batches[1].ShouldContain("COLUMNPROPERTY", Case.Insensitive);
        step.Batches[1].ShouldContain("ALTER TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_at NVARCHAR(64) NULL", Case.Insensitive);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0032_ShapeJson_MatchesSql(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 32 && s.Name == StepName);
        var deltas = steps.ToDictionary(s => s.Version, s => s.ShapeDelta);

        step.ShapeDelta.Ops.Count.ShouldBe(2);
        step.ShapeDelta.Ops.ShouldAllBe(op =>
            op.Op == "alterColumn" &&
            string.Equals(op.Table, "FUNCTIONAL_RIGHT_MAPPINGS", StringComparison.OrdinalIgnoreCase) &&
            op.Column != null &&
            op.Column.Nullable);
        step.ShapeDelta.Ops.Select(op => op.Column!.Name).ShouldBe(["approved_by", "approved_at"]);
        foreach (var op in step.ShapeDelta.Ops)
        {
            step.Sql.ShouldContain(op.Column!.Name, Case.Insensitive);
        }

        var before = GovernanceSchemaShapeLoader.LoadAt(engine, 31, deltas);
        var beforeTable = before.Tables.Single(t => string.Equals(t.Name, "FUNCTIONAL_RIGHT_MAPPINGS", StringComparison.OrdinalIgnoreCase));
        var beforeApprovedBy = beforeTable.Columns.Single(c => c.Name == "approved_by");
        var beforeApprovedAt = beforeTable.Columns.Single(c => c.Name == "approved_at");
        beforeApprovedBy.Nullable.ShouldBeFalse();
        beforeApprovedAt.Nullable.ShouldBeFalse();

        var after = GovernanceSchemaShapeLoader.LoadAt(engine, 32, deltas);
        var afterTable = after.Tables.Single(t => string.Equals(t.Name, "FUNCTIONAL_RIGHT_MAPPINGS", StringComparison.OrdinalIgnoreCase));
        var afterApprovedBy = afterTable.Columns.Single(c => c.Name == "approved_by");
        var afterApprovedAt = afterTable.Columns.Single(c => c.Name == "approved_at");
        afterApprovedBy.Nullable.ShouldBeTrue();
        afterApprovedAt.Nullable.ShouldBeTrue();
        afterApprovedBy.Type.ShouldBe(beforeApprovedBy.Type);
        afterApprovedAt.Type.ShouldBe(beforeApprovedAt.Type);
        afterApprovedBy.Family.ShouldBe(beforeApprovedBy.Family);
        afterApprovedAt.Family.ShouldBe(beforeApprovedAt.Family);
    }

    private const string StepName = "functional_right_mapping_pending_approval";

    private static SqlMigrationStep Step(string engine) =>
        SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 32 && s.Name == StepName);

    private static void AssertMatchesGoldenChecksum(string engine, SqlMigrationStep step)
    {
        var checksumsPath = Path.Combine(FindRepoRoot(), "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
        var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith($"0032 {StepName} ", StringComparison.Ordinal));
        step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
