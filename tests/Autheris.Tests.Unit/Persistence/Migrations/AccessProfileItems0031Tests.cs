using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class AccessProfileItems0031Tests
{
    private const string StepName = "access_profile_items";

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0031_PassesLint_AndMatchesShape(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 31 && s.Name == StepName);
        step.Phase3Marker.ShouldBe("MIG-0031");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 31, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        var profiles = shape.Tables.Single(t => string.Equals(t.Name, "access_profiles", StringComparison.OrdinalIgnoreCase));
        profiles.Columns.ShouldNotContain(c => string.Equals(c.Name, "masking_mode", StringComparison.OrdinalIgnoreCase));
        profiles.Columns.ShouldNotContain(c => string.Equals(c.Name, "target_tables", StringComparison.OrdinalIgnoreCase));
        var items = shape.Tables.Single(t => string.Equals(t.Name, "access_profile_items", StringComparison.OrdinalIgnoreCase));
        items.PrimaryKey.ShouldBe(["tenant_id", "profile_id", "item_no"]);
        items.Indexes.Single().Unique.ShouldBeTrue();
        shape.Tables.Single(t => string.Equals(t.Name, "access_profile_classification_access", StringComparison.OrdinalIgnoreCase))
            .PrimaryKey.ShouldBe(["tenant_id", "profile_id", "code"]);
    }

    [Fact]
    public void Step0031_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0031 " + StepName + " ", StringComparison.Ordinal));
            Step(engine).Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    [Theory]
    [InlineData("postgresql", "unknown masking mode in access profile")]
    [InlineData("sqlserver", "unknown masking mode in access profile")]
    public void Backfill_UnknownMaskingMode_Aborts(string engine, string message)
    {
        var sql = Step(engine).Sql;
        AbortComesBeforeBackfillAndDrop(sql, message);
        sql.ShouldContain(engine == "sqlserver" ? "NOT IN (N'default', N'unmasked', N'strict')" : "NOT IN ('default', 'unmasked', 'strict')");
        sql.ShouldContain(engine == "sqlserver" ? "THROW 51030" : "CAST('MIG-0031 aborted");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Backfill_InvalidTargetTablesJson_Aborts(string engine)
    {
        var sql = Step(engine).Sql;
        AbortComesBeforeBackfillAndDrop(sql, "target tables are not a JSON array in access profile");
        sql.ShouldContain(engine == "sqlserver" ? "ISJSON(target_tables) = 0" : "IS JSON ARRAY");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Backfill_BlankOrOverlongEntry_Aborts(string engine)
    {
        var sql = Step(engine).Sql;
        AbortComesBeforeBackfillAndDrop(sql, "blank, overlong or non-string target table entry in access profile");
        sql.ShouldContain(engine == "sqlserver" ? "DATALENGTH(e.value) > 1024" : "LENGTH(e.value #>> '{}') > 512");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void StepHeader_StatesAllNodesRunNewBuildFirst(string engine)
    {
        var sql = Step(engine).Sql;
        var header = sql[..Index(sql, "\n\n")];
        header.ShouldContain("Rolling upgrade: all nodes must run this build before the step applies");
        header.ShouldContain("no older node may be left running");
        header.ShouldContain("No dual write, no read fallback to the old columns");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void StepSql_BothTablesHaveTenantIdInPkAndFk(string engine)
    {
        var sql = Step(engine).Sql;
        sql.ShouldContain("(tenant_id, profile_id, item_no)");
        sql.ShouldContain("(tenant_id, profile_id, code)");
        sql.Split("FOREIGN KEY (tenant_id, profile_id)").Length.ShouldBe(3, "both tables reference the profile by (tenant_id, profile_id)");
        sql.ShouldContain("ON DELETE CASCADE");
        sql.ShouldContain("CHECK (access_level IN (0, 1, 2))");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void StepSql_AbortMessageNamesProfileIdOnly(string engine)
    {
        var sql = Step(engine).Sql;
        sql.ShouldNotContain("target_tables ||", Case.Insensitive);
        sql.ShouldNotContain("+ @entry", Case.Insensitive);
        sql.ShouldNotContain("e.value ||", Case.Insensitive);
        sql.ShouldNotContain("object_ref ||", Case.Insensitive);
        var concatenations = sql.Split('\n')
            .Where(l => l.Contains("MIG-0031 aborted", StringComparison.Ordinal))
            .ToList();
        concatenations.Count.ShouldBe(3);
        concatenations.ShouldAllBe(l => engine == "sqlserver" ? l.Contains("+ @first", StringComparison.Ordinal) : l.Contains("|| f.profile_id", StringComparison.Ordinal));
    }

    private static SqlMigrationStep Step(string engine) =>
        SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 31 && s.Name == StepName);

    private static int Index(string sql, string text) => sql.IndexOf(text, StringComparison.Ordinal);

    private static void AbortComesBeforeBackfillAndDrop(string sql, string message)
    {
        var abort = Index(sql, message);
        abort.ShouldBeGreaterThan(0, message);
        abort.ShouldBeLessThan(sql.IndexOf("INSERT INTO ", Index(sql, "MIG-0031 aborted"), StringComparison.Ordinal));
        abort.ShouldBeLessThan(Index(sql, "DROP COLUMN"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
