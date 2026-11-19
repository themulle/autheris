using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DropRoleGrantee0029Tests
{
    private const string StepName = "drop_role_grantee";

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0029_ChecksBeforeItChangesAnything_IsIdempotent_AndMatchesShape(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 29 && s.Name == StepName);
        step.Phase3Marker.ShouldBe("MIG-0029");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
        step.Sql.ShouldContain("All nodes must run this build before the step applies");

        // The three pre-checks (consents, virtual filter profiles, profile assignments) come before the first delete and the first drop.
        var sql = step.Sql;
        var firstDelete = sql.IndexOf("DELETE FROM", StringComparison.Ordinal);
        var firstDrop = sql.IndexOf(engine == "sqlserver" ? "DROP INDEX" : "DROP INDEX IF EXISTS", StringComparison.Ordinal);
        firstDelete.ShouldBeGreaterThan(0);
        foreach (var check in new[] { "active role grantee in consent", "active role grantee in virtual filter access profile", "active role grantee in access profile assignment" })
        {
            var at = sql.IndexOf(check, StringComparison.Ordinal);
            at.ShouldBeGreaterThanOrEqualTo(0, check);
            at.ShouldBeLessThan(firstDelete, check);
        }

        // Child rows go before their consents, the index before the columns, and the abort message names no role or tenant data.
        sql.IndexOf("DELETE FROM " + (engine == "sqlserver" ? "dbo.CONSENT_COLUMN_RULES" : "consent_column_rules"), StringComparison.Ordinal)
            .ShouldBeLessThan(sql.IndexOf("DELETE FROM " + (engine == "sqlserver" ? "dbo.CONSENTS" : "consents"), StringComparison.Ordinal));
        sql.IndexOf("DELETE FROM " + (engine == "sqlserver" ? "dbo.CONSENT_ROW_FILTERS" : "consent_row_filters"), StringComparison.Ordinal)
            .ShouldBeLessThan(sql.IndexOf("DELETE FROM " + (engine == "sqlserver" ? "dbo.CONSENTS" : "consents"), StringComparison.Ordinal));
        firstDrop.ShouldBeGreaterThan(firstDelete);
        sql.ShouldContain(engine == "sqlserver" ? "COL_LENGTH" : "DROP COLUMN IF EXISTS");
        sql.ShouldNotContain("role_name' +", Case.Insensitive);
        sql.ShouldNotContain("tenant_id ||", Case.Insensitive);

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 29, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        var consents = shape.Tables.Single(t => string.Equals(t.Name, "consents", StringComparison.OrdinalIgnoreCase));
        consents.Columns.ShouldNotContain(c => string.Equals(c.Name, "role_id", StringComparison.OrdinalIgnoreCase));
        consents.Columns.ShouldNotContain(c => string.Equals(c.Name, "role_name", StringComparison.OrdinalIgnoreCase));
        consents.Indexes.ShouldNotContain(i => string.Equals(i.Name, "ix_consents_tenant_role", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Step0029_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 29 && s.Name == StepName);
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0029 " + StepName + " ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
