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
    public async Task Run_OnFreshDatabase_CreatesKeyAndIndexDefinitionTables()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);

        await NewRunner(repository, cs).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('TABLE_PRIMARY_KEY_COLUMNS', 'TABLE_INDEXES', 'TABLE_INDEX_COLUMNS')").ShouldBe(3);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_PRIMARY_KEY_COLUMNS') WHERE name IN ('table_id','key_ordinal','table_column_id')").ShouldBe(3);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_PRIMARY_KEY_COLUMNS') WHERE pk > 0").ShouldBe(2);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEXES') WHERE name IN ('id','table_id','index_name','is_unique')").ShouldBe(4);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEXES') WHERE name = 'id' AND pk = 1").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEX_COLUMNS') WHERE name IN ('index_id','column_ordinal','table_column_id')").ShouldBe(3);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEX_COLUMNS') WHERE pk > 0").ShouldBe(2);
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('UX_TABLE_PK_COLUMNS_COLUMN','UX_TABLE_INDEXES_NAME','UX_TABLE_INDEX_COLUMNS_COLUMN') AND sql LIKE 'CREATE UNIQUE INDEX%'").ShouldBe(3);
    }

    [Fact]
    public void BaselineMigrationChecksums_AreUnchanged()
    {
        static string Expected(string engine) => MigrationStep.ComputeChecksum(engine + ":0001:baseline_governance_schema:v1");

        new SqliteGovernanceMigrationRunner(NewRawRepository(NewConnectionString()), Options.Create(Gateway(NewConnectionString())))
            .Steps[0].Checksum.ShouldBe(Expected("sqlite"));
        // Pinned literal values guard against accidental changes to the identity string.
        Expected("sqlite").ShouldBe(PinnedChecksum("sqlite"));
        Expected("postgresql").ShouldBe(PinnedChecksum("postgresql"));
        Expected("sqlserver").ShouldBe(PinnedChecksum("sqlserver"));
    }

    private static string PinnedChecksum(string engine) => engine switch
    {
        "sqlite" => "5B2722FAAB8D29BE6B490F9A1B18BA3BEF882260EBF6225706E55349647575AA",
        "postgresql" => "486B0B352CCA7CEF31BC3EEF6693D89CAB858D47DB6A593F2C47F61F51FE1815",
        "sqlserver" => "28F8528ACFAB5F5463B761C2F23F5BC1F29079F701D78F1665D74EA4A02D05B9",
        _ => throw new ArgumentOutOfRangeException(nameof(engine))
    };

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

        var runner = NewRunner(repository, cs);
        await runner.RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TABLES', 'AUDIT_LOG_ENTRIES', 'CONSENTS', 'autheris_schema_migrations')").ShouldBe(4);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(runner.Steps.Count);
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

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(runner.Steps.Count);
    }

    [Fact]
    public async Task Run_BaselineCreatesWebhookOutboxAndCdcWatermarks()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        var runner = NewRunner(repository, cs);

        runner.Steps.Count.ShouldBe(2);
        runner.Steps[0].ShouldSatisfyAllConditions(
            s => s.Version.ShouldBe(1),
            s => s.Name.ShouldBe("baseline_governance_schema"));
        runner.Steps[1].Name.ShouldBe("key_and_index_column_direction");
        await runner.RunAsync();
        await runner.RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1 AND name = 'baseline_governance_schema' AND length(checksum) = 64").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('WEBHOOK_OUTBOX', 'ux_webhook_outbox_dedupe', 'ix_webhook_outbox_due', 'ix_webhook_outbox_lease', 'ix_webhook_outbox_owner', 'autheris_cdc_watermarks')").ShouldBe(6);
    }

    [Fact]
    public async Task Run_BaselineCreatesCloudEventSubscriptions()
    {
        var cs = NewConnectionString();
        using var repository = NewRawRepository(cs);
        var runner = NewRunner(repository, cs);

        await runner.RunAsync();
        await runner.RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('CLOUDEVENT_SUBSCRIPTIONS', 'ix_cloudevent_subs_tenant')").ShouldBe(2);
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
        Exec(repository, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (3, 'future', 'x', 'now', 'test')");

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
