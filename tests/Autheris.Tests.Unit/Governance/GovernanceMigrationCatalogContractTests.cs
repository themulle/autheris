using System.Globalization;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #388 (WP-388-1) / WP-SIMPL-11: engine-neutral parts of the forward-only migration engine and the step catalogues of the
/// server engines. The engine behaviour against a real database (checksum, gap, lock, drift, upgrade) is covered by
/// <c>GovernanceUpgradeTestsBase</c> on PostgreSQL and SQL Server in the integration tests.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceMigrationCatalogContractTests
{
    private const string Probe = "PROBE_388";

    private static GovernanceSchemaShapeDelta ProbeDelta { get; } = new(
    [
        new ShapeOp(
            "addTable",
            Probe,
            NewTable: new SchemaTable(
                Probe,
                [new SchemaColumn("id", "TEXT", SchemaTypeFamily.String), new SchemaColumn("note", "TEXT", SchemaTypeFamily.String, Nullable: true)],
                ["id"],
                []))
    ]);

    private static string PinnedChecksum(string engine) => engine switch
    {
        "postgresql" => "486B0B352CCA7CEF31BC3EEF6693D89CAB858D47DB6A593F2C47F61F51FE1815",
        "sqlserver" => "28F8528ACFAB5F5463B761C2F23F5BC1F29079F701D78F1665D74EA4A02D05B9",
        _ => throw new ArgumentOutOfRangeException(nameof(engine))
    };

    [Fact]
    public void DriftReport_IsCappedAndHasNoConnectionDetails()
    {
        var deviations = Enumerable.Range(0, 120)
            .Select(i => new SchemaDeviation(SchemaDeviationKind.MissingTable, "T" + i.ToString(CultureInfo.InvariantCulture), null, string.Empty, null))
            .ToArray();

        var message = GovernanceSchemaShapeComparer.FormatMessage("PostgreSQL", "governance", [1], [], deviations);

        message.ShouldContain("... and 70 more");
        message.ShouldNotContain("Password", Case.Insensitive);
        message.Split('\n').Count(l => l.Contains("missing table", StringComparison.Ordinal)).ShouldBe(50);
    }

    [Fact]
    public void Shape_WrongNullability_Detected()
    {
        var expected = new GovernanceSchemaShape("postgresql", [new SchemaTable("T", [new SchemaColumn("c", "TEXT", SchemaTypeFamily.String)], [], [])]);
        var actual = new GovernanceSchemaShape("postgresql", [new SchemaTable("T", [new SchemaColumn("c", "TEXT", SchemaTypeFamily.String, Nullable: true)], [], [])]);

        var deviations = GovernanceSchemaShapeComparer.Compare(expected, actual);

        deviations.ShouldHaveSingleItem().Kind.ShouldBe(SchemaDeviationKind.WrongNullability);
    }

    [Fact]
    public void SqlServerAppLock_NegativeReturnCodes_FailClosed()
    {
        SqlServerGovernanceMigrationRunner.ApplockFailure(-1, 300).Code.ShouldBe(GovernanceMigrationCodes.LockTimeout);
        foreach (var rc in new[] { -2, -3, -999 })
        {
            SqlServerGovernanceMigrationRunner.ApplockFailure(rc, 300).Code.ShouldBe(GovernanceMigrationCodes.LockFailed);
        }
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void LockTimeoutSeconds_IsRangeValidated(int seconds, bool valid)
    {
        var options = new GovernanceMigrationOptions { LockTimeoutSeconds = seconds };

        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options, new System.ComponentModel.DataAnnotations.ValidationContext(options), null, validateAllProperties: true).ShouldBe(valid);
    }

    [Fact]
    public void ShapeDelta_FoldsOntoV0001_AndFailsClosedOnInconsistency()
    {
        var shape = GovernanceSchemaShapeLoader.LoadAt("postgresql", 2, new Dictionary<int, GovernanceSchemaShapeDelta> { [2] = ProbeDelta });

        shape.Tables.ShouldContain(t => t.Name == Probe);
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.LoadAt("postgresql", 2, new Dictionary<int, GovernanceSchemaShapeDelta>()));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeFolder.Fold(shape, new GovernanceSchemaShapeDelta([new ShapeOp("dropTable", "NO_SUCH")])));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeFolder.Fold(shape, ProbeDelta));
    }

    [Fact]
    public void EveryEmbeddedStep_LoadsAndIsNumberedConsecutively_ForTheServerEngines()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            steps.Select(s => s.Version).ShouldBe(Enumerable.Range(2, steps.Count), $"{engine} steps must be numbered consecutively from 0002");
            steps.Select(s => s.Name).Distinct().Count().ShouldBe(steps.Count);
        }
    }

    [Fact]
    public void Phase3Markers_ReferenceAnIdThatExistsInThePlanRegistry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var plans = string.Concat(Directory.EnumerateFiles(Path.Combine(dir.FullName, "docs", "plans"), "*.md").Select(File.ReadAllText));
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            foreach (var step in SqlMigrationStep.LoadEmbedded(engine).Where(s => s.Phase3Marker is not null))
            {
                plans.ShouldContain(step.Phase3Marker!, Case.Sensitive, $"{engine} step {step.Version:D4} cites an unknown phase-3 condition");
            }
        }
    }

    [Fact]
    public void BaselineMigrationChecksums_AreUnchanged()
    {
        static string Expected(string engine) => MigrationStep.ComputeChecksum(engine + ":0001:baseline_governance_schema:v1");

        // Pinned literal values guard against accidental changes to the identity string.
        Expected("postgresql").ShouldBe(PinnedChecksum("postgresql"));
        Expected("sqlserver").ShouldBe(PinnedChecksum("sqlserver"));
    }

    [Fact]
    public void Catalogues_AreGapFreeAndStartAtOne_ForTheServerProviders()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var pgOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=autheris_test;Username=u;Password=p" }
        });
        var sqlServerOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = "Server=localhost;Database=autheris_test;User Id=u;Password=p;TrustServerCertificate=true" }
        });
        using var pg = GovernanceStoreFactory.CreateUnmigratedPostgreSql(Substitute.For<IEpochValidationService>(), pgOptions, env);
        using var mssql = GovernanceStoreFactory.CreateUnmigratedSqlServer(Substitute.For<IEpochValidationService>(), sqlServerOptions, env);

        var catalogues = new[]
        {
            new PostgreSqlGovernanceMigrationRunner(pg, pgOptions).Steps,
            new SqlServerGovernanceMigrationRunner(mssql, sqlServerOptions).Steps
        };

        foreach (var steps in catalogues)
        {
            steps.Select(s => s.Version).ShouldBe(Enumerable.Range(1, steps.Count));
            steps.ShouldAllBe(s => s.Checksum.Length == 64);
        }
    }

    [Fact]
    public async Task HostedService_RunsMigrationsBeforeInitialization()
    {
        var runner = Substitute.For<IGovernanceMigrationRunner>();
        var initializer = Substitute.For<IGovernanceStoreInitializer>();
        var service = new GovernanceMigrationHostedService(runner, initializer);

        await service.StartAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            runner.RunAsync(Arg.Any<CancellationToken>());
            initializer.InitializeAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HostedService_WhenMigrationFails_StartupFailsAndStoreIsNotInitialized()
    {
        var runner = Substitute.For<IGovernanceMigrationRunner>();
        runner.RunAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("migration failed")));
        var initializer = Substitute.For<IGovernanceStoreInitializer>();
        var service = new GovernanceMigrationHostedService(runner, initializer);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        await initializer.DidNotReceive().InitializeAsync(Arg.Any<CancellationToken>());
    }
}
