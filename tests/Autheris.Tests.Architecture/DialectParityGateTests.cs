namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using Xunit;

public sealed class DialectGateTests
{
    private static readonly TargetSqlDialect[] ExpectedTargetDialects =
    [
        TargetSqlDialect.Ansi,
        TargetSqlDialect.PostgreSql,
        TargetSqlDialect.SqlServer,
        TargetSqlDialect.Sqlite,
        TargetSqlDialect.DuckDb,
        TargetSqlDialect.Oracle,
        TargetSqlDialect.Databricks
    ];

    private static readonly DatabaseDialect[] ExpectedDatabaseDialects =
    [
        DatabaseDialect.PostgreSql,
        DatabaseDialect.SqlServer,
        DatabaseDialect.Sqlite,
        DatabaseDialect.Databricks,
        DatabaseDialect.Oracle
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Dialect_set_is_closed()
    {
        var actualTarget = Enum.GetValues<TargetSqlDialect>();
        actualTarget.OrderBy(d => d).ShouldBe(
            ExpectedTargetDialects.OrderBy(d => d),
            "WP-SIMPL-21: TargetSqlDialect is closed; adding a new dialect requires an explicit architecture review and parity lane.");

        var actualDatabase = Enum.GetValues<DatabaseDialect>();
        actualDatabase.OrderBy(d => d).ShouldBe(
            ExpectedDatabaseDialects.OrderBy(d => d),
            "WP-SIMPL-21: DatabaseDialect is closed; adding a new database dialect requires an explicit architecture review and parity lane.");

        foreach (var dialect in actualTarget)
        {
            var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
            generator.ShouldNotBeNull();
            generator.TargetDialect.ShouldBe(dialect);
        }
    }
}

public sealed class DialectParityGateTests
{
    private static readonly TargetSqlDialect[] NonAnsiDialects =
    [
        TargetSqlDialect.PostgreSql,
        TargetSqlDialect.SqlServer,
        TargetSqlDialect.Sqlite,
        TargetSqlDialect.DuckDb,
        TargetSqlDialect.Oracle,
        TargetSqlDialect.Databricks
    ];

    private static readonly string[] CoreParityDialects =
    [
        "postgres",
        "sqlserver",
        "sqlite",
        "duckdb"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Every_generator_has_a_parity_baseline_file()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        File.Exists(Path.Combine(root, "tests", "Autheris.Tests.Parity", "parity-baseline.json")).ShouldBeTrue();
        File.Exists(Path.Combine(root, "tests", "Autheris.Tests.Parity", "parity-baseline-dialects.json")).ShouldBeTrue();

        foreach (var dialect in NonAnsiDialects)
        {
            var fixturePath = Path.Combine(root, "tests", "TrinoSqlEngine.Tests", "Conformance", "Fixtures", $"{dialect}.json");
            File.Exists(fixturePath).ShouldBeTrue(
                $"WP-SIMPL-21: Dialect generator '{dialect}' must have a conformance baseline fixture at 'tests/TrinoSqlEngine.Tests/Conformance/Fixtures/{dialect}.json'.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Every_core_dialect_has_a_pr_parity_lane()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var dialectTestsText = File.ReadAllText(Path.Combine(root, "tests", "Autheris.Tests.Parity", "Dialects", "DialectTests.cs"));
        var matrixTestsText = File.ReadAllText(Path.Combine(root, "tests", "Autheris.Tests.Parity", "Matrix", "MatrixTests.cs"));
        var duckDbCoreTestsPath = Path.Combine(root, "tests", "Autheris.Tests.Integration", "Olap", "DuckDbCoreSecurityTests.cs");
        var federationTestsPath = Path.Combine(root, "tests", "Autheris.Tests.Integration", "Federation", "FederationSecurityTests.cs");

        dialectTestsText.ShouldContain("\"postgres\"");
        dialectTestsText.ShouldContain("\"sqlserver\"");
        File.Exists(duckDbCoreTestsPath).ShouldBeTrue("WP-SIMPL-21: DuckDbCoreSecurityTests must exist for DuckDB parity.");
        File.Exists(federationTestsPath).ShouldBeTrue("WP-SIMPL-21: FederationSecurityTests must exist for DuckDB parity.");
        matrixTestsText.ShouldContain("ParityHostFixture");

        var ciYaml = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        ciYaml.ShouldContain("pull_request:");
        (ciYaml.Contains("Autheris.Tests.SqlCompilerGate", StringComparison.Ordinal) ||
         ciYaml.Contains("parity-pr", StringComparison.Ordinal)).ShouldBeTrue(
            "WP-SIMPL-21: PR workflow (.github/workflows/ci.yml) must execute the SQL compiler/parity gate lane.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DuckDb_parity_lane_runs_on_pr()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var duckDbCoreTestsPath = Path.Combine(root, "tests", "Autheris.Tests.Integration", "Olap", "DuckDbCoreSecurityTests.cs");
        var federationTestsPath = Path.Combine(root, "tests", "Autheris.Tests.Integration", "Federation", "FederationSecurityTests.cs");
        File.Exists(duckDbCoreTestsPath).ShouldBeTrue();
        File.Exists(federationTestsPath).ShouldBeTrue();

        var duckDbCoreText = File.ReadAllText(duckDbCoreTestsPath);
        duckDbCoreText.ShouldContain("class DuckDbCoreSecurityTests");
        var federationText = File.ReadAllText(federationTestsPath);
        federationText.ShouldContain("class FederationSecurityTests");

        var ciYaml = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        ciYaml.ShouldContain("pull_request:");
        (ciYaml.Contains("Autheris.Tests.Integration", StringComparison.Ordinal) ||
         ciYaml.Contains("DuckDbCoreSecurityTests", StringComparison.Ordinal)).ShouldBeTrue(
            "WP-SIMPL-21: PR workflow (.github/workflows/ci.yml) must run DuckDB parity/integration tests on pull_request.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_pending_cell_for_core_dialects()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselinePath = Path.Combine(root, "tests", "Autheris.Tests.Parity", "parity-baseline-dialects.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(baselinePath));

        if (doc.RootElement.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in cells.EnumerateObject())
            {
                var key = prop.Name;
                var status = prop.Value.GetString() ?? string.Empty;
                if (CoreParityDialects.Any(d => key.Contains($"|{d}", StringComparison.OrdinalIgnoreCase)))
                {
                    status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                        $"WP-SIMPL-21: Core dialect parity cell '{key}' must not be PENDING in parity-baseline-dialects.json (was '{status}').");
                }
            }
        }

        var dialectTestsText = File.ReadAllText(Path.Combine(root, "tests", "Autheris.Tests.Parity", "Dialects", "DialectTests.cs"));
        dialectTestsText.ShouldNotContain("sqlserver_host");
    }
}
