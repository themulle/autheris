using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class ClassificationMigration0020Tests
{
    [Fact]
    public void Migration_0020_Passes_StepSqlLint_And_Matches_ShapeJson()
    {
        var repoRoot = FindRepoRoot();
        var registeredNames = Enum.GetValues<GovernanceTable>()
            .Select(t => t.ToSqlName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        registeredNames.ShouldContain("DATA_CLASSIFICATIONS");
        registeredNames.ShouldContain("CLASSIFICATION_MASK_RULES");
        registeredNames.ShouldContain("COLUMN_CLASSIFICATIONS");
        registeredNames.ShouldContain("COLUMN_MASK_OVERRIDES");

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 20 && s.Name == "data_classifications");
            step.Phase3Marker.ShouldBe("MIG-0020");

            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var checksumsPath = Path.Combine(
                repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var recordedLine = File.ReadAllLines(checksumsPath)
                .Single(l => l.StartsWith("0020 data_classifications ", StringComparison.Ordinal));
            var recordedSha = recordedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(recordedSha);

            var shape = GovernanceSchemaShapeLoader.LoadAt(
                engine,
                20,
                steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

            var classifications = shape.Tables.Single(
                t => string.Equals(t.Name, "DATA_CLASSIFICATIONS", StringComparison.OrdinalIgnoreCase));
            classifications.PrimaryKey.ShouldBe(["scope", "code"]);

            var maskRules = shape.Tables.Single(
                t => string.Equals(t.Name, "CLASSIFICATION_MASK_RULES", StringComparison.OrdinalIgnoreCase));
            maskRules.PrimaryKey.ShouldBe(["scope", "code"]);
            maskRules.Columns.Any(c => string.Equals(c.Name, "catalog_text", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

            var colClassifications = shape.Tables.Single(
                t => string.Equals(t.Name, "COLUMN_CLASSIFICATIONS", StringComparison.OrdinalIgnoreCase));
            colClassifications.PrimaryKey.ShouldBe(["scope", "table_column_id", "code"]);
            colClassifications.Indexes.Any(
                i => string.Equals(i.Name, "ix_column_classifications_col", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

            var maskOverrides = shape.Tables.Single(
                t => string.Equals(t.Name, "COLUMN_MASK_OVERRIDES", StringComparison.OrdinalIgnoreCase));
            maskOverrides.PrimaryKey.ShouldBe(["id"]);
            maskOverrides.Columns.Any(c => string.Equals(c.Name, "catalog_text", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
            maskOverrides.Indexes.Any(
                i => string.Equals(i.Name, "ux_column_mask_overrides_code", StringComparison.OrdinalIgnoreCase) && i.Unique).ShouldBeTrue();
            maskOverrides.Indexes.Any(
                i => string.Equals(i.Name, "ux_column_mask_overrides_all", StringComparison.OrdinalIgnoreCase) && i.Unique).ShouldBeTrue();

            foreach (var token in new[]
                     {
                         "PUBLIC", "CONFIDENTIAL", "FINANCIAL", "PII", "PII_SPECIAL", "CREDENTIALS",
                         "_DEFAULT", "TYPE_SAFE_REDACT", "STEWARD_UNCONFIGURED",
                         "NULLIFY", "HMAC_SHA256", "MASK_EMAIL", "MASK_IBAN", "PARTIAL_MASK", "GEO_JITTER", "TOKENIZE"
                     })
            {
                step.Sql.ShouldContain(token);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
