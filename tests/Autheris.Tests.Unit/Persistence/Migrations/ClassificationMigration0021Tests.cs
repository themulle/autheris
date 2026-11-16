namespace Autheris.Tests.Unit.Persistence.Migrations;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationMigration0021Tests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Migration0021_ExistsAndPassesStepSqlLintAndShapeContract(string provider)
    {
        var root = FindRepoRoot();
        var stepsDir = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", provider);
        var sqlPath = Path.Combine(stepsDir, "0021_consent_classification_access.sql");
        var shapePath = Path.Combine(stepsDir, "0021_consent_classification_access.shape.json");

        File.Exists(sqlPath).ShouldBeTrue($"Missing step SQL: {sqlPath}");
        File.Exists(shapePath).ShouldBeTrue($"Missing shape JSON: {shapePath}");

        var sql = File.ReadAllText(sqlPath);
        var normalized = SqlMigrationStep.Normalize(sql);
        var violations = StepSqlLint.Validate(normalized, guardSql: null, phase3Marker: "MIG-0021");
        violations.ShouldBeEmpty();

        using var doc = JsonDocument.Parse(File.ReadAllText(shapePath));
        var ops = doc.RootElement.GetProperty("ops");
        var addTable = ops[0];
        addTable.GetProperty("op").GetString().ShouldBe("addTable");
        addTable.GetProperty("table").GetString().ShouldBe("consent_classification_access", StringCompareShould.IgnoreCase);
        var cols = addTable.GetProperty("newTable").GetProperty("columns");
        var colNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in cols.EnumerateArray())
        {
            colNames.Add(col.GetProperty("name").GetString()!);
        }

        colNames.ShouldContain("tenant_id");
        colNames.ShouldContain("consent_id");
        colNames.ShouldContain("code");
        colNames.ShouldContain("access_level");
        colNames.ShouldContain("valid_until");
        colNames.ShouldContain("approved_by_sid");
        colNames.ShouldContain("approved_at");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Migration0021_ChecksumMatchesGoldenFile(string provider)
    {
        var root = FindRepoRoot();
        var sqlPath = Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", provider, "0021_consent_classification_access.sql");
        var goldenPath = Path.Combine(root, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{provider}.txt");

        var sql = File.ReadAllText(sqlPath);
        var canonical = SqlMigrationStep.Normalize(sql);
        var hash = SqlMigrationStep.ComputeChecksum(provider, 21, "consent_classification_access", canonical);

        var lines = File.ReadAllLines(goldenPath);
        lines.ShouldContain($"0021 consent_classification_access {hash}");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Migration0021_DefinesKeysAndBackfillParityRules(string provider)
    {
        var root = FindRepoRoot();
        var sql = File.ReadAllText(Path.Combine(root, "src", "Autheris.Infrastructure", "Persistence", "Migrations", "Steps", provider, "0021_consent_classification_access.sql"));

        sql.ShouldContain("CONSENT_CLASSIFICATION_ACCESS", Case.Insensitive);
        sql.ShouldContain("PRIMARY KEY", Case.Insensitive);
        sql.ShouldContain("CONSENTS", Case.Insensitive);
        sql.ShouldContain("'PII'", Case.Sensitive);
        sql.ShouldContain("CONSENT_COLUMN_RULES", Case.Insensitive);
        sql.ShouldContain("COLUMN_CLASSIFICATIONS", Case.Insensitive);
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

        throw new DirectoryNotFoundException("Could not locate Autheris.sln.");
    }
}
