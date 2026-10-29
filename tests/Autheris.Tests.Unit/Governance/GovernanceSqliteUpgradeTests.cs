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
        using var repository = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
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
        await new SqliteGovernanceMigrationRunner(repository, options) { AdditionalSteps = [step] }.RunAsync();

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
        using var repository = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        using (var cmd = repository.RawConnection().CreateCommand())
        {
            cmd.CommandText = "DROP TABLE CONSUMER_APP_DAILY";
            cmd.ExecuteNonQuery();
        }

        var before = Text(repository, "SELECT group_concat(type || ':' || name || ':' || ifnull(sql, ''), '|') FROM (SELECT * FROM sqlite_master ORDER BY name)");
        var trace = new List<string>();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => new SqliteGovernanceMigrationRunner(repository, options) { AdditionalSteps = [ProbeStep()], Trace = trace.Add }.RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty();
        Text(repository, "SELECT group_concat(type || ':' || name || ':' || ifnull(sql, ''), '|') FROM (SELECT * FROM sqlite_master ORDER BY name)").ShouldBe(before);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(1);
    }
}
