namespace Autheris.Tests.Unit.Persistence.Migrations;

using System;
using System.IO;
using System.Linq;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DropGovernanceMigration0023And0024Tests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0023_DropsLegacySensitivityObjects_AfterIdempotentBackfill(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 23 && s.Name == "drop_is_sensitive");
        step.Phase3Marker.ShouldBe("MIG-0023");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        var sql = step.Sql;
        var backfillPii = sql.IndexOf("COLUMN_CLASSIFICATIONS", StringComparison.OrdinalIgnoreCase);
        var backfillMask = sql.IndexOf("INTO " + (engine == "sqlserver" ? "dbo." : string.Empty) + "COLUMN_MASK_OVERRIDES", StringComparison.OrdinalIgnoreCase);
        var dropRules = sql.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase);
        var dropFlag = sql.IndexOf("DROP COLUMN", StringComparison.OrdinalIgnoreCase);
        backfillPii.ShouldBeGreaterThanOrEqualTo(0);
        backfillMask.ShouldBeGreaterThan(backfillPii);
        dropRules.ShouldBeGreaterThan(backfillMask, "nothing is dropped before the backfill ran");
        dropFlag.ShouldBeGreaterThan(dropRules);
        sql.ShouldContain("NOT EXISTS", Case.Insensitive);
        sql.ShouldContain("<> " + (engine == "sqlserver" ? "N'PUBLIC'" : "'PUBLIC'"), Case.Insensitive);

        // A legacy DENY rule keeps denying: mapped to NULLIFY flagged deny, and repaired for rows the 0020 backfill made visible.
        var denyMap = engine == "sqlserver" ? "WHEN N'DENY' THEN N'NULLIFY'" : "WHEN 'DENY' THEN 'NULLIFY'";
        sql.ShouldContain(denyMap);
        sql.ShouldContain("{\"deny\":true}");
        var repair = sql.IndexOf("Repair:", StringComparison.Ordinal);
        repair.ShouldBeGreaterThan(backfillMask);
        sql.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase).ShouldBeGreaterThan(repair);
        sql.ShouldContain("system:migration-0020");

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 23, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        shape.Tables.ShouldNotContain(t => string.Equals(t.Name, "column_masking_rules", StringComparison.OrdinalIgnoreCase));
        shape.Tables.Single(t => string.Equals(t.Name, "table_columns", StringComparison.OrdinalIgnoreCase))
            .Columns.ShouldNotContain(c => string.Equals(c.Name, "is_sensitive", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0024_PreCheckRunsBeforeAnyDrop_AndDropsOnlyDeadObjects(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 24 && s.Name == "drop_superseded_governance_columns");
        step.Phase3Marker.ShouldBe("MIG-0024");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

        var sql = step.Sql;
        var check = sql.IndexOf("role_id IS NOT NULL", StringComparison.OrdinalIgnoreCase);
        check.ShouldBeGreaterThanOrEqualTo(0);
        sql.ShouldContain("is_revoked = 0", Case.Insensitive);
        check.ShouldBeLessThan(sql.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase), "an unrevoked role-based consent aborts before anything is dropped");
        if (engine == "sqlserver")
        {
            sql.ShouldContain("THROW", Case.Insensitive);
        }

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 24, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        string[] gone = ["roles", "role_members", "access_policies", "access_policy_grantees", "access_policy_selectors"];
        foreach (var table in gone)
        {
            shape.Tables.ShouldNotContain(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        }

        var filters = shape.Tables.Single(t => string.Equals(t.Name, "consent_row_filters", StringComparison.OrdinalIgnoreCase)).Columns.Select(c => c.Name.ToLowerInvariant()).ToList();
        filters.ShouldNotContain("dependent_table");
        filters.ShouldNotContain("additional_hops_json");
        filters.ShouldContain("key_set_strategy");
        filters.ShouldContain("key_set_max_keys");

        // Still read or written by the code base: not dropped.
        shape.Tables.Single(t => string.Equals(t.Name, "consents", StringComparison.OrdinalIgnoreCase)).Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("role_id");
        shape.Tables.Single(t => string.Equals(t.Name, "consent_requests", StringComparison.OrdinalIgnoreCase)).Columns.Select(c => c.Name.ToLowerInvariant()).ShouldContain("itsm_ticket_id");
        shape.Tables.Select(t => t.Name.ToLowerInvariant()).ShouldContain("recert_campaigns");
        shape.Tables.Select(t => t.Name.ToLowerInvariant()).ShouldContain("recert_items");
    }

    [Fact]
    public void DroppedTables_AreNoLongerGovernanceTables_OrRequiredAtStartup()
    {
        var names = Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dropped in new[] { "COLUMN_MASKING_RULES", "ROLES", "ROLE_MEMBERS", "ACCESS_POLICIES", "ACCESS_POLICY_GRANTEES", "ACCESS_POLICY_SELECTORS" })
        {
            names.ShouldNotContain(dropped);
            GovernanceGlobalTables.IsGlobal(dropped).ShouldBeFalse();
            GovernanceSchemaValidator.RequiredWave2Tables.ShouldNotContain(dropped);
        }
    }

    [Theory]
    [InlineData("postgresql", 23, "drop_is_sensitive")]
    [InlineData("sqlserver", 23, "drop_is_sensitive")]
    [InlineData("postgresql", 24, "drop_superseded_governance_columns")]
    [InlineData("sqlserver", 24, "drop_superseded_governance_columns")]
    public void Checksums_MatchGoldenFile(string engine, int version, string name)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == version && s.Name == name);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var golden = File.ReadAllLines(Path.Combine(dir.FullName, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt"))
            .Single(l => l.StartsWith($"{version:D4} {name} ", StringComparison.Ordinal));
        golden.Split(' ')[2].ShouldBe(step.Checksum);
    }
}
