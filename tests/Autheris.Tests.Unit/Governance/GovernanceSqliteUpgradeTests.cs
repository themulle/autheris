using System.Globalization;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #388 (WP-388-3, I388-T-04/T-05 on SQLite): a v1.5.0 database with data and a verified audit chain upgrades through a test-only
/// additive step without touching the data; an unsupported base is rejected before any statement runs. PostgreSQL and SQL Server run the
/// same contract in the integration tests (<c>*GovernanceUpgradeTests</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceSqliteUpgradeTests
{
    private const string StepName = "test_add_probe_column";

    private static IOptions<GatewayOptions> Opts(string cs) =>
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = cs, SeedDemoData = false } });

    private static MigrationStep ProbeStep() =>
        new(
            2,
            StepName,
            SqlMigrationStep.ComputeChecksum("sqlite", 2, StepName, "v1"),
            ctx =>
            {
                using var cmd = ctx.Connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE CONSENTS ADD COLUMN probe_388 TEXT NULL";
                cmd.ExecuteNonQuery();
                return Task.CompletedTask;
            },
            Transactional: true)
        {
            ShapeDelta = new GovernanceSchemaShapeDelta(
                [new ShapeOp("addColumn", "CONSENTS", Column: new SchemaColumn("probe_388", "TEXT", SchemaTypeFamily.String, Nullable: true))])
        };

    private static long Scalar(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Text(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public async Task I388_T_04_Upgrade_FromV150Baseline_AppliesAllSteps_KeepsData_AuditChainAndAnchorVerify()
    {
        var cs = $"Data Source=up_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Opts(cs);
        using var repository = NewUnmigrated(options);
        await BaselineRunner(repository, options).RunAsync();
        await repository.InitializeAsync();
        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("src388", "dbo", "readings");
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "readings", TableName = "readings", SchemaName = "dbo", SourceType = "Sqlite", SourceName = "src388" },
            Identifier = identifier,
            Columns = [new TableColumn { TableId = tableId, ColumnName = "client_id", DataType = "varchar" }],
            PrimaryKeyColumns = ["client_id"]
        });
        await repository.CreateConsentAsync(new Consent
        {
            TableId = tableId,
            TableIdentifier = identifier,
            TenantId = "tenant-388",
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role,
            RoleName = "Reader",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "client_id", AccessLevel = ColumnAccessLevel.Clear }]
        });
        for (var i = 0; i < 25; i++)
        {
            await repository.RecordAuditEventAsync(new AuditLogEntry
            {
                EventType = "UPGRADE_388",
                ActorSid = new Sid("S-1-388-" + i),
                TargetTable = "dbo.t",
                TraceId = Guid.NewGuid().ToString("N")
            });
        }

        await repository.FlushAuditChannelAsync();
        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
        var consents = Scalar(repository, "SELECT COUNT(*) FROM CONSENTS");
        var audit = Scalar(repository, "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES");
        var hashes = Text(repository, "SELECT group_concat(entry_hash, ',') FROM (SELECT entry_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq)");
        consents.ShouldBe(1);
        audit.ShouldBeGreaterThanOrEqualTo(25);

        var step = ProbeStep();
        await new SqliteGovernanceMigrationRunner(repository, options) { AdditionalSteps = [step], StepVersionCapOverride = 1 }.RunAsync();

        Scalar(repository, $"SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2 AND name = '{StepName}' AND checksum = '{step.Checksum}'").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('CONSENTS') WHERE name = 'probe_388'").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM CONSENTS").ShouldBe(consents);
        Scalar(repository, "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES").ShouldBe(audit);
        Text(repository, "SELECT group_concat(entry_hash, ',') FROM (SELECT entry_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq)").ShouldBe(hashes, "no step reads or rewrites audit rows");
        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue("the audit hash chain and anchor verify after the upgrade");
    }

    [Fact]
    public async Task I388_T_05_Adoption_PreV150Database_WithHistory_FailsUnsupportedBase_AndExecutesNoDdl()
    {
        var cs = $"Data Source=up_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Opts(cs);
        using var repository = NewUnmigrated(options);
        await BaselineRunner(repository, options).RunAsync();
        await repository.InitializeAsync();
        using (var cmd = repository.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DROP TABLE CONSUMER_APP_DAILY";
            cmd.ExecuteNonQuery();
        }

        var before = Text(repository, "SELECT group_concat(type || ':' || name || ':' || ifnull(sql, ''), '|') FROM (SELECT * FROM sqlite_master ORDER BY name)");
        var trace = new List<string>();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => new SqliteGovernanceMigrationRunner(repository, options) { AdditionalSteps = [ProbeStep()], StepVersionCapOverride = 1, Trace = trace.Add }.RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty();
        Text(repository, "SELECT group_concat(type || ':' || name || ':' || ifnull(sql, ''), '|') FROM (SELECT * FROM sqlite_master ORDER BY name)").ShouldBe(before);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(1);
    }

    private const string KeyDirectionStepName = "key_and_index_column_direction";

    private static SqliteGovernanceMigrationRunner BaselineRunner(SqliteGovernanceRepository repository, IOptions<GatewayOptions> options) =>
        new(repository, options) { StepVersionCapOverride = 1 };

    private static SqliteGovernanceRepository NewUnmigrated(IOptions<GatewayOptions> options) =>
        GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), options);

    private static void Exec(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task SeedV150KeysAndAuditAsync(SqliteGovernanceRepository repository)
    {
        var tableId = Guid.NewGuid();
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "readings", TableName = "readings", SchemaName = "dbo", SourceType = "Sqlite", SourceName = "src390" },
            Identifier = new TableIdentifier("src390", "dbo", "readings"),
            Columns = [new TableColumn { TableId = tableId, ColumnName = "device_id", DataType = "varchar" }, new TableColumn { TableId = tableId, ColumnName = "ts", DataType = "datetime" }]
        });
        // v1.5.0 shape: the key tables have no is_descending column, so the rows are written with the v1 column list.
        Exec(repository, $"INSERT INTO TABLE_PRIMARY_KEY_COLUMNS (table_id, key_ordinal, table_column_id) VALUES ('{tableId}', 1, 'c1'), ('{tableId}', 2, 'c2')");
        Exec(repository, "INSERT INTO TABLE_INDEX_COLUMNS (index_id, column_ordinal, table_column_id) VALUES ('i1', 1, 'c1'), ('i1', 2, 'c2')");
        for (var i = 0; i < 25; i++)
        {
            await repository.RecordAuditEventAsync(new AuditLogEntry
            {
                EventType = "UPGRADE_390",
                ActorSid = new Sid("S-1-390-" + i),
                TargetTable = "dbo.t",
                TraceId = Guid.NewGuid().ToString("N")
            });
        }

        await repository.FlushAuditChannelAsync();
    }

    [Fact]
    public async Task I390_T_01_Step0002_UpgradeFromV150_WithData_PreservesRowsAndAuditChain_DefaultsToAscending()
    {
        var cs = $"Data Source=up390_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Opts(cs);
        using var repository = NewUnmigrated(options);
        await BaselineRunner(repository, options).RunAsync();
        await repository.InitializeAsync();
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_PRIMARY_KEY_COLUMNS') WHERE name = 'is_descending'").ShouldBe(0, "the baseline is exactly v1.5.0");
        await SeedV150KeysAndAuditAsync(repository);
        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue();
        var audit = Scalar(repository, "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES");
        var hashes = Text(repository, "SELECT group_concat(entry_hash, ',') FROM (SELECT entry_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq)");

        await new SqliteGovernanceMigrationRunner(repository, options).RunAsync();

        var checksum = SqlMigrationStep.LoadEmbedded("sqlite").Single(s => s.Version == 2).Checksum;
        Scalar(repository, $"SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2 AND name = '{KeyDirectionStepName}' AND checksum = '{checksum}'").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS").ShouldBe(2);
        Scalar(repository, "SELECT COUNT(*) FROM TABLE_PRIMARY_KEY_COLUMNS WHERE is_descending = 0").ShouldBe(2);
        Scalar(repository, "SELECT COUNT(*) FROM TABLE_INDEX_COLUMNS WHERE is_descending = 0").ShouldBe(2);
        Scalar(repository, "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES").ShouldBe(audit);
        Text(repository, "SELECT group_concat(entry_hash, ',') FROM (SELECT entry_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq)").ShouldBe(hashes);
        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue("the audit hash chain and anchor verify after step 0002");
    }

    [Fact]
    public async Task Step0002_Rerun_IsIdempotent_AndTheGuardSkipsTheStatements()
    {
        var cs = $"Data Source=up390_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Opts(cs);
        using var repository = NewUnmigrated(options);
        await new SqliteGovernanceMigrationRunner(repository, options).RunAsync();
        Exec(repository, "DELETE FROM autheris_schema_migrations WHERE version = 2");

        await new SqliteGovernanceMigrationRunner(repository, options).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('TABLE_INDEX_COLUMNS') WHERE name = 'is_descending'").ShouldBe(1);
    }

    [Fact]
    public async Task Step0002_InsertWithV1ColumnList_ReadsAscending_AndCheckRejectsOtherValues()
    {
        var cs = $"Data Source=up390_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Opts(cs);
        using var repository = NewUnmigrated(options);
        await new SqliteGovernanceMigrationRunner(repository, options).RunAsync();

        Exec(repository, "INSERT INTO TABLE_PRIMARY_KEY_COLUMNS (table_id, key_ordinal, table_column_id) VALUES ('t', 1, 'c')");

        Scalar(repository, "SELECT is_descending FROM TABLE_PRIMARY_KEY_COLUMNS WHERE table_id = 't'").ShouldBe(0);
        Should.Throw<Exception>(() => Exec(repository, "INSERT INTO TABLE_PRIMARY_KEY_COLUMNS (table_id, key_ordinal, table_column_id, is_descending) VALUES ('t', 2, 'd', 2)"));
    }
}
