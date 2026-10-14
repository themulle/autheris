using System.Globalization;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-ARC-B-12: versioned governance schema migrations; repository constructors run no DDL; startup fails closed.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaMigrationTests
{
    private static GatewayOptions Gateway(
        string connectionString,
        GovernanceMigrationMode mode = GovernanceMigrationMode.Apply,
        bool allowNewerSchema = false) => new()
    {
        GovernanceDb = new GovernanceDbOptions
        {
            ConnectionString = connectionString,
            SeedDemoData = false,
            Migrations = new GovernanceMigrationOptions { Mode = mode, AllowNewerSchema = allowNewerSchema }
        }
    };

    private static string NewConnectionString() => $"Data Source=mig_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static SqliteGovernanceRepository NewRawRepository(string connectionString) =>
        GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), Options.Create(Gateway(connectionString)));

    private static SqliteGovernanceMigrationRunner NewRunner(
        SqliteGovernanceRepository repository,
        string connectionString,
        GovernanceMigrationMode mode = GovernanceMigrationMode.Apply,
        bool allowNewerSchema = false) =>
        new(repository, Options.Create(Gateway(connectionString, mode, allowNewerSchema)));

    private static long Scalar(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Constructor_RunsNoDdl()
    {
        using var repository = NewRawRepository(NewConnectionString());

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'").ShouldBe(0);
    }

    [Fact]
    public async Task Run_OnFreshDatabase_CreatesSchemaAndRecordsBaseline()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);

        await NewRunner(repository, cs).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TABLES', 'AUDIT_LOG_ENTRIES', 'CONSENTS', 'autheris_schema_migrations')").ShouldBe(4);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1 AND name = 'baseline_governance_schema' AND length(checksum) = 64").ShouldBe(1);
    }

    [Fact]
    public async Task Run_Twice_IsIdempotent()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        var runner = NewRunner(repository, cs);

        await runner.RunAsync();
        await runner.RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(1);
    }

    [Fact]
    public async Task Run_OnPreExistingDatabaseWithoutVersionTable_AdoptsBaselineAndKeepsData()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES ('o1', 'S-1-5-21-1', 'CORP\\o1', 'Owner', 'o1@corp.local', 1)");
        Exec(repository, "DROP TABLE autheris_schema_migrations");

        await NewRunner(repository, cs).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM DATA_OWNERS WHERE id = 'o1'").ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithChecksumDrift_Throws()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "UPDATE autheris_schema_migrations SET checksum = 'tampered' WHERE version = 1");

        await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());
    }

    [Fact]
    public async Task Run_WhenDatabaseIsNewerThanBinary_FailsUnlessAllowed()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (99, 'future', 'x', 'now', 'test')");

        await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());
        await NewRunner(repository, cs, allowNewerSchema: true).RunAsync();
    }

    [Fact]
    public async Task Run_InVerifyMode_FailsOnUnmigratedDatabaseAndPassesAfterApply()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);

        await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync());
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'").ShouldBe(0);

        await NewRunner(repository, cs).RunAsync();
        await NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync();
    }

    [Fact]
    public async Task Run_InSkipMode_ChangesNothing()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);

        await NewRunner(repository, cs, GovernanceMigrationMode.Skip).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'").ShouldBe(0);
    }

    [Fact]
    public async Task Initialize_BeforeMigration_FailsClosed_AndSucceedsAfterwards()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);

        await Should.ThrowAsync<SqliteException>(async () => await repository.InitializeAsync());

        await NewRunner(repository, cs).RunAsync();
        await repository.InitializeAsync();
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

    [Fact]
    public void Catalogues_AreGapFreeAndStartAtOne_ForAllProviders()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var sqliteOptions = Options.Create(Gateway(NewConnectionString()));
        var pgOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = "Host=localhost;Database=autheris_test;Username=u;Password=p" }
        });
        var sqlServerOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = "Server=localhost;Database=autheris_test;User Id=u;Password=p;TrustServerCertificate=true" }
        });
        using var sqlite = GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), sqliteOptions, env);
        using var pg = GovernanceStoreFactory.CreateUnmigratedPostgreSql(Substitute.For<IEpochValidationService>(), pgOptions, env);
        using var mssql = GovernanceStoreFactory.CreateUnmigratedSqlServer(Substitute.For<IEpochValidationService>(), sqlServerOptions, env);

        var catalogues = new[]
        {
            new SqliteGovernanceMigrationRunner(sqlite, sqliteOptions).Steps,
            new PostgreSqlGovernanceMigrationRunner(pg, pgOptions).Steps,
            new SqlServerGovernanceMigrationRunner(mssql, sqlServerOptions).Steps
        };

        foreach (var steps in catalogues)
        {
            steps.Select(s => s.Version).ShouldBe(Enumerable.Range(1, steps.Count));
            steps.ShouldAllBe(s => s.Checksum.Length == 64);
        }
    }
}
