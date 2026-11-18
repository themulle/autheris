using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DropLegacyGovernanceColumns0027Tests
{
    private const string StepName = "drop_legacy_governance_columns";

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Step0027_IsIdempotent_DropsTicketIndexBeforeColumn_AndMatchesShape(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 27 && s.Name == StepName);
        step.Phase3Marker.ShouldBe("MIG-0027");
        StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
        step.Sql.ShouldContain("All nodes must run this build before the step applies");

        var sql = step.Sql;
        var dropIndex = sql.IndexOf("ux_consent_requests_ticket", StringComparison.Ordinal);
        var dropTicket = sql.IndexOf("itsm_ticket_id", StringComparison.Ordinal);
        dropIndex.ShouldBeGreaterThanOrEqualTo(0);
        dropTicket.ShouldBeGreaterThan(dropIndex, "the dependent index goes before the column");
        sql.ShouldContain(engine == "sqlserver" ? "COL_LENGTH" : "DROP COLUMN IF EXISTS");

        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 27, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        var requests = shape.Tables.Single(t => string.Equals(t.Name, "consent_requests", StringComparison.OrdinalIgnoreCase));
        requests.Columns.ShouldNotContain(c => string.Equals(c.Name, "itsm_ticket_id", StringComparison.OrdinalIgnoreCase));
        requests.Indexes.ShouldNotContain(i => string.Equals(i.Name, "ux_consent_requests_ticket", StringComparison.OrdinalIgnoreCase));
        shape.Tables.Single(t => string.Equals(t.Name, "table_catalog_settings", StringComparison.OrdinalIgnoreCase))
            .Columns.ShouldNotContain(c => string.Equals(c.Name, "visibility", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Step0027_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 27 && s.Name == StepName);
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0027 " + StepName + " ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    [Fact]
    public void LegacyGovernanceColumns_AreNotReferenced()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var migrations = Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar;
        var ticket = new Regex(@"itsm_ticket_id", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var visibilityColumn = new Regex(@"(table_id,\s*visibility\b|SET\s+visibility\b|""@vis"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            // Frozen baseline text and historical steps legitimately name the columns.
            var inMigrations = file.Contains(migrations, StringComparison.Ordinal);
            var text = File.ReadAllText(file);
            if (!inMigrations && ticket.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(srcRoot, file) + " (itsm_ticket_id)");
            }

            var touchesSettings = text.Contains("TABLE_CATALOG_SETTINGS", StringComparison.Ordinal) || text.Contains("SettingsTable", StringComparison.Ordinal);
            if (!inMigrations && touchesSettings && visibilityColumn.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(srcRoot, file) + " (TABLE_CATALOG_SETTINGS.visibility)");
            }
        }

        offenders.ShouldBeEmpty("The columns were dropped by step 0027; no SQL may select, insert or update them again.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
