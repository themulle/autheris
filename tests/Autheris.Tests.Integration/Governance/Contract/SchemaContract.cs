using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Common;
using FluentAssertions;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic contract suite for <c>IGovernanceMigrationRunner</c>, <c>IGovernanceStoreInitializer</c>,
/// and <c>IGovernanceRepository</c> schema readiness, drift detection, and post-condition verification.
/// </summary>
public abstract class SchemaContract : GovernanceContractBase
{
    private static readonly Sid SchemaActor = new("S-1-5-21-600-1");

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Store.Repository.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "SchemaReadIsoA",
            ActorSid = SchemaActor,
            TargetTable = "schema.dbo.iso",
            Decision = "ALLOW",
            TenantId = Store.TenantA
        });
        await Store.Repository.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "SchemaReadIsoB",
            ActorSid = SchemaActor,
            TargetTable = "schema.dbo.iso",
            Decision = "ALLOW",
            TenantId = Store.TenantB
        });

        var logsA = await Store.Repository.GetAuditLogEntriesAsync(limit: 50, tenantId: Store.TenantA);
        logsA.Should().OnlyContain(e => e.TenantId == Store.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var id = new TableIdentifier("schema", "dbo", "write_iso_" + Guid.NewGuid().ToString("N")[..8]);
        _ = await Store.Repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                IsActive = true
            },
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "id",
                    DataType = "int",
                    IsSensitive = false
                }
            ]
        });

        var consentA = await Store.Repository.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = id,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = SchemaActor,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantA
        });

        var pageB = await Store.Repository.GetConsentPageAsync(new ConsentPageQuery(Store.TenantB, Table: id));
        pageB.Items.Should().BeEmpty();
        (await Store.Repository.GetConsentByIdAsync(consentA.Id))!.TenantId.Should().Be(Store.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        var act = () => Store.Repository.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "MissingTenant",
            ActorSid = SchemaActor,
            TargetTable = "schema.dbo.t",
            Decision = "DENY",
            TenantId = default
        });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        await Store.MigrationRunner.ApplyAsync();
        await Store.MigrationRunner.ApplyAsync();
        await Store.MigrationRunner.VerifyAsync();
        await Store.StoreInitializer.InitializeAsync();
        await Store.StoreInitializer.InitializeAsync();

        await using var seeded1 = await Store.CreateAdditionalStoreAsync(
            seedDemoData: true,
            runMigrations: true,
            initializeStore: true);
        var count1 = (await seeded1.TableMetadata.GetAllTablesAsync()).Count;
        count1.Should().BeGreaterThan(0);

        await using var seeded2 = await Store.CreateAdditionalStoreAsync(
            seedDemoData: true,
            runMigrations: true,
            initializeStore: true);
        var count2 = (await seeded2.TableMetadata.GetAllTablesAsync()).Count;
        count2.Should().Be(count1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var emptyConnStr = await Store.CreateEmptyDatabaseConnectionStringAsync("checksum_drift");
        await using var migrated = await GovernanceTestStore.CreateFromConnectionStringAsync(
            Store.Provider,
            emptyConnStr,
            runMigrations: true,
            initializeStore: false);

        await migrated.ExecuteNonQueryAsync(
            "UPDATE autheris_schema_migrations SET checksum = 'TAMPERED_CHECKSUM' WHERE version = 1");

        var verifyAct = () => migrated.MigrationRunner.VerifyAsync();
        var ex = await verifyAct.Should().ThrowAsync<GovernanceSchemaDriftException>();
        ex.Which.Code.Should().Be(GovernanceMigrationCodes.ChecksumMismatch);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        await using var conn = await Store.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM autheris_schema_migrations ORDER BY version";
        var versions = new List<int>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                versions.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
        }

        versions.Should().NotBeEmpty();
        versions.Should().BeInAscendingOrder();
        versions[0].Should().Be(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var page = await Store.Repository.QueryAuditLogPageAsync(
            new AuditLogPageQuery(Store.TenantA, PageSize: AuditLogPageQuery.MaxPageSize + 2000));
        page.Items.Count.Should().BeLessOrEqualTo(AuditLogPageQuery.MaxPageSize);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        await using var uninitialized = await Store.CreateAdditionalStoreAsync(
            runMigrations: false,
            initializeStore: false);

        var act = () => uninitialized.Repository.RecordAuditEventAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "BeforeInit",
            ActorSid = SchemaActor,
            TargetTable = "schema.dbo.uninit",
            Decision = "DENY",
            TenantId = Store.TenantA
        });
        await act.Should().ThrowAsync<GovernanceStoreNotReadyException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Verify_on_empty_database_fails_closed_with_Pending_code_and_redacts_secrets()
    {
        if (!Store.IsAvailable) return;

        var emptyConnStr = await Store.CreateEmptyDatabaseConnectionStringAsync("empty_verify");
        await using var unmigrated = await GovernanceTestStore.CreateFromConnectionStringAsync(
            Store.Provider,
            emptyConnStr,
            runMigrations: false,
            initializeStore: false);

        var act = () => unmigrated.MigrationRunner.VerifyAsync();
        var ex = await act.Should().ThrowAsync<GovernanceSchemaDriftException>();
        ex.Which.Code.Should().Be(GovernanceMigrationCodes.Pending);
        ex.Which.Message.Should().NotContain(emptyConnStr);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Pre_created_drifted_table_fails_closed_on_Apply_and_Verify()
    {
        if (!Store.IsAvailable) return;

        var emptyConnStr = await Store.CreateEmptyDatabaseConnectionStringAsync("drifted_tbl");
        await using var drifted = await GovernanceTestStore.CreateFromConnectionStringAsync(
            Store.Provider,
            emptyConnStr,
            runMigrations: false,
            initializeStore: false);

        await drifted.ExecuteNonQueryAsync("CREATE TABLE TABLES (id VARCHAR(64) NOT NULL PRIMARY KEY)");

        var applyAct = () => drifted.MigrationRunner.ApplyAsync();
        var applyEx = await applyAct.Should().ThrowAsync<GovernanceSchemaDriftException>();
        applyEx.Which.Code.Should().Be(GovernanceMigrationCodes.Drift);

        var verifyAct = () => drifted.MigrationRunner.VerifyAsync();
        var verifyEx = await verifyAct.Should().ThrowAsync<GovernanceSchemaDriftException>();
        verifyEx.Which.Code.Should().Be(GovernanceMigrationCodes.Pending);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Tampered_audit_trigger_fails_closed_during_post_condition_verification()
    {
        if (!Store.IsAvailable) return;

        var emptyConnStr = await Store.CreateEmptyDatabaseConnectionStringAsync("trigger_tamper");
        await using var target = await GovernanceTestStore.CreateFromConnectionStringAsync(
            Store.Provider,
            emptyConnStr,
            runMigrations: true,
            initializeStore: false);

        if (target.IsSqlServer)
        {
            await target.ExecuteNonQueryAsync("DISABLE TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES");
        }
        else
        {
            await target.ExecuteNonQueryAsync("DROP TRIGGER trg_audit_append_only ON AUDIT_LOG_ENTRIES");
        }

        var verifyAct = () => target.MigrationRunner.VerifyAsync();
        var ex = await verifyAct.Should().ThrowAsync<GovernanceSchemaDriftException>();
        ex.Which.Message.Should().Contain("audit trigger");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlSchemaContractTests(GovernancePostgresFixture fixture) : SchemaContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "schema_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerSchemaContractTests(GovernanceSqlServerFixture fixture) : SchemaContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "schema_contract");
}
