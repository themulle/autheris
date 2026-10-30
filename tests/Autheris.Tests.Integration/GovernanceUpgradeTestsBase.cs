namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #388 (WP-388-3, plan D-10): upgrade-path, adoption, concurrency, lock-timeout, history-integrity, per-step drift and
/// least-privilege tests against the real PostgreSQL and SQL Server engines. No real step exists after the v1.5.0 baseline yet, so a
/// test-only step <c>0002_test_add_probe_column</c> (one additive nullable column on CONSENTS) is injected through the internal seam.
/// The dialect subclasses supply the SQL; the assertions are identical so that both engines are held to the same contract.
/// </summary>
[Trait("Category", "Security")]
public abstract class GovernanceUpgradeTestsBase
{
    protected const string ProbeColumn = "probe_388";
    private const string StepName = "test_add_probe_column";

    protected sealed record TestDb(string ConnectionString, Func<ValueTask> Dispose);

    protected abstract string Engine { get; }
    protected abstract string ConsentsTable { get; }
    protected abstract string ProbeColumnType { get; }
    protected abstract string AddProbeColumnSql { get; }
    protected abstract string ProbeColumnCountSql { get; }
    protected abstract string TriggerCountSql { get; }

    /// <summary>Number of column defaults on <c>tenant_id</c> of the given table (0 = no default).</summary>
    protected abstract string TenantDefaultCountSql(string table);

    /// <summary>1 when <c>CONSENT_REQUESTS.tenant_id</c> is nullable, 0 when it is NOT NULL.</summary>
    protected abstract string RequestTenantNullableSql { get; }
    protected abstract string AuditHashesSql { get; }
    protected abstract string FingerprintSql { get; }
    protected abstract string DropSomeBaselineTableSql { get; }
    protected abstract string ForeignOldShapeTableSql { get; }
    protected abstract string HistoryTableCountSql { get; }

    protected abstract Task<TestDb?> NewDbAsync();
    protected abstract Task ExecAsync(string connectionString, string sql);
    protected abstract Task<string> QueryAsync(string connectionString, string sql);

    /// <summary>Runs the engine runner with the given options; the step set is the whole shipped catalogue plus <paramref name="extra"/>, or only the frozen baseline (step 1) with <paramref name="baselineOnly"/>.</summary>
    protected abstract Task RunAsync(
        string connectionString,
        GovernanceMigrationMode mode,
        IReadOnlyList<MigrationStep>? extra = null,
        bool allowNewer = false,
        int lockTimeoutSeconds = 300,
        bool requireLeastPrivilege = false,
        List<string>? log = null,
        List<string>? trace = null,
        bool baselineOnly = false,
        int? stepVersionCap = null);

    /// <summary>Creates the current-schema database through the real catalogue and seeds consents, catalog metadata and a verified audit chain.</summary>
    protected abstract Task SeedV150Async(string connectionString);

    /// <summary>Creates a database that holds exactly the v1.5.0 schema (baseline step only) with a verified audit chain; no repository call touches the key tables.</summary>
    protected abstract Task SeedBaselineAsync(string connectionString);

    /// <summary>Opens the store on the upgraded database (catalogue plus <paramref name="extra"/>) and verifies the audit chain and anchor.</summary>
    protected abstract Task<bool> AuditChainVerifiesAsync(string connectionString, IReadOnlyList<MigrationStep> extra);

    protected abstract Task<IAsyncDisposable> HoldSchemaLockAsync(string connectionString);

    /// <summary>A login that can read the schema; with <paramref name="writeOnHistory"/> it can also write the history table (excess privilege).</summary>
    protected abstract Task<(string ConnectionString, Func<Task> Cleanup)> CreateReadOnlyLoginAsync(TestDb db, bool writeOnHistory);

    protected MigrationStep ProbeStep(string seed = "v1", Action? onApply = null, bool wrongNullability = false)
    {
        var sql = AddProbeColumnSql;
        return new MigrationStep(
            5,
            StepName,
            SqlMigrationStep.ComputeChecksum(Engine, 5, StepName, seed + sql),
            ctx =>
            {
                onApply?.Invoke();
                using var cmd = ctx.Connection.CreateCommand();
                cmd.Transaction = ctx.Transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return Task.CompletedTask;
            },
            Transactional: true)
        {
            ShapeDelta = new GovernanceSchemaShapeDelta(
            [
                new ShapeOp("addColumn", ConsentsTable, Column: new SchemaColumn(ProbeColumn, ProbeColumnType, SchemaTypeFamily.String, Nullable: !wrongNullability))
            ])
        };
    }

    private async Task<long> CountAsync(string cs, string sql) => long.Parse(await QueryAsync(cs, sql), System.Globalization.CultureInfo.InvariantCulture);

    protected static async Task SeedCoreAsync(IGovernanceRepository repository, Func<Task> flushAudit)
    {
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
                TenantId = "tenant-388",
                EventType = "UPGRADE_388",
                ActorSid = new Sid("S-1-388-" + i),
                TargetTable = "dbo.t",
                TraceId = Guid.NewGuid().ToString("N")
            });
        }

        await flushAudit();
        (await repository.VerifyAuditHashChainAsync()).ShouldBeTrue("the seeded v1.5.0 audit chain must verify before the upgrade");
    }

    private async Task WithDbAsync(Func<TestDb, Task> body)
    {
        var db = await NewDbAsync();
        if (db is null)
        {
            return; // no Docker on this machine (the fixture fails the run when containers are required)
        }

        try
        {
            await body(db);
        }
        finally
        {
            await db.Dispose();
        }
    }

    /// <summary>A database that holds exactly the v1.5.0 schema (step 1, applied by the real catalogue).</summary>
    private async Task MigrateBaselineAsync(TestDb db, bool baselineOnly = false) => await RunAsync(db.ConnectionString, GovernanceMigrationMode.Apply, baselineOnly: baselineOnly);

    [Fact]
    public Task I388_T_04_Upgrade_FromV150Baseline_AppliesAllSteps_KeepsData_AuditChainAndAnchorVerify_AndTriggerEnabled() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedV150Async(cs);
        var consents = await CountAsync(cs, $"SELECT COUNT(*) FROM {ConsentsTable}");
        var auditCount = await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries");
        var auditHashes = await QueryAsync(cs, AuditHashesSql);
        var triggersBefore = await CountAsync(cs, TriggerCountSql);
        consents.ShouldBe(1);
        auditCount.ShouldBeGreaterThanOrEqualTo(25);
        triggersBefore.ShouldBeGreaterThan(0);
        var step = ProbeStep();

        await RunAsync(cs, GovernanceMigrationMode.Apply, [step]);

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 5 AND name = '" + StepName + "' AND checksum = '" + step.Checksum + "'")).ShouldBe(1);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(1);
        (await CountAsync(cs, $"SELECT COUNT(*) FROM {ConsentsTable}")).ShouldBe(consents);
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "no step may read or rewrite audit rows");
        (await CountAsync(cs, TriggerCountSql)).ShouldBe(triggersBefore, "the append-only audit triggers stay enabled");
        (await AuditChainVerifiesAsync(cs, [step])).ShouldBeTrue("the audit hash chain and anchor verify after the upgrade");

        // A second run and a Verify run are no-ops on the upgraded database.
        await RunAsync(cs, GovernanceMigrationMode.Apply, [step]);
        await RunAsync(cs, GovernanceMigrationMode.Verify, [step]);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(5);
    });

    private const string SeedV1KeyRowsSql =
        "INSERT INTO table_primary_key_columns (table_id, key_ordinal, table_column_id) VALUES ('t390', 1, 'c1'), ('t390', 2, 'c2')";

    private const string SeedV1IndexRowsSql =
        "INSERT INTO table_index_columns (index_id, column_ordinal, table_column_id) VALUES ('i390', 1, 'c1'), ('i390', 2, 'c2')";

    [Fact]
    public Task I390_T_01_Step0002_UpgradeFromV150_WithData_PreservesRowsAndAuditChain_DefaultsToAscending() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedBaselineAsync(cs);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(1, "the seeded database holds exactly the v1.5.0 baseline");
        await ExecAsync(cs, SeedV1KeyRowsSql);
        await ExecAsync(cs, SeedV1IndexRowsSql);
        var auditCount = await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries");
        var auditHashes = await QueryAsync(cs, AuditHashesSql);
        var checksum = SqlMigrationStep.LoadEmbedded(Engine).Single(s => s.Version == 2).Checksum;

        await RunAsync(cs, GovernanceMigrationMode.Apply, stepVersionCap: 2);

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2 AND name = 'key_and_index_column_direction' AND checksum = '" + checksum + "'")).ShouldBe(1);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_primary_key_columns")).ShouldBe(2);
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_primary_key_columns")).ShouldBe(0, "existing key rows read as ascending");
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_index_columns")).ShouldBe(2);
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_index_columns")).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "step 0002 does not read or rewrite audit rows");
        (await CountAsync(cs, TriggerCountSql)).ShouldBeGreaterThan(0, "the append-only audit triggers stay enabled");
        // Rerun after the history row is lost (capped at step 2, so the schema is not yet past step 0003): the guarded statements are idempotent and the shape still verifies.
        await ExecAsync(cs, "DELETE FROM autheris_schema_migrations WHERE version >= 2");
        await RunAsync(cs, GovernanceMigrationMode.Apply, stepVersionCap: 2);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2")).ShouldBe(1);
        await RunAsync(cs, GovernanceMigrationMode.Verify, stepVersionCap: 2);

        (await AuditChainVerifiesAsync(cs, [])).ShouldBeTrue("the audit hash chain and anchor verify after step 0002");

        // A v1.5.0 binary (AllowNewerSchema rollback) inserts without the column and reads ascending.
        await ExecAsync(cs, "INSERT INTO table_primary_key_columns (table_id, key_ordinal, table_column_id) VALUES ('t390b', 1, 'c9')");
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_primary_key_columns WHERE table_id = 't390b'")).ShouldBe(0);

    });

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I404-SC-10")]
    public Task I404_T_04_Step0004_UpgradeFromV150_AppliesAndShapeVerifies() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedBaselineAsync(cs);
        await ExecAsync(cs, SeedV1KeyRowsSql);
        var auditCount = await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries");
        var auditHashes = await QueryAsync(cs, AuditHashesSql);
        var checksum = SqlMigrationStep.LoadEmbedded(Engine).Single(s => s.Version == 4).Checksum;

        await RunAsync(cs, GovernanceMigrationMode.Apply);

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 4 AND name = 'physical_nonunique_indexes' AND checksum = '" + checksum + "'")).ShouldBe(1);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_physical_indexes")).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_physical_index_columns")).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_primary_key_columns")).ShouldBe(2, "additive: existing rows are untouched");
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "step 0004 does not read or rewrite audit rows");
        await Should.ThrowAsync<Exception>(() => ExecAsync(cs, "INSERT INTO table_physical_indexes (id, table_id, index_ordinal) VALUES ('p0', 'no-such-table', 1)"), "the foreign key refuses a physical index of an unknown table");
        await ExecAsync(cs, "INSERT INTO tables (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active) VALUES ('t404', 'sql', 'd', 's', 't', 't', 'NORMAL', 0, 1)");
        await ExecAsync(cs, "INSERT INTO table_physical_indexes (id, table_id, index_ordinal) VALUES ('p1', 't404', 1)");
        await ExecAsync(cs, "INSERT INTO table_physical_index_columns (index_id, column_ordinal, table_column_id) VALUES ('p1', 1, 'c1')");
        await Should.ThrowAsync<Exception>(() => ExecAsync(cs, "INSERT INTO table_physical_indexes (id, table_id, index_ordinal) VALUES ('p2', 't404', 1)"), "the ordinal is unique per table");
        await Should.ThrowAsync<Exception>(() => ExecAsync(cs, "INSERT INTO table_physical_indexes (id, table_id, index_ordinal) VALUES ('p3', 't404', 0)"));
        await Should.ThrowAsync<Exception>(() => ExecAsync(cs, "INSERT INTO table_physical_index_columns (index_id, column_ordinal, table_column_id) VALUES ('no-such-index', 1, 'c1')"));

        // I404 Phase 5: removing the table row cannot leave stale physical rows that a reused id would resurface.
        await ExecAsync(cs, "DELETE FROM tables WHERE id = 't404'");
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_physical_indexes")).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_physical_index_columns")).ShouldBe(0);
        await RunAsync(cs, GovernanceMigrationMode.Verify);
    });

    private static string InsertRequestSql(string id, string tenantLiteral) =>
        "INSERT INTO consent_requests (id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref, business_justification, status, requested_at, requested_valid_to, tenant_id) " +
        $"VALUES ('{id}', 't', 'S-1', 'Role', 'R', 'j', 'Pending', '2026-01-01', '2026-02-01', {tenantLiteral})";

    [Fact]
    public Task WPDT2b_Step0003_UpgradeFromV150_WithData_DropsDefaults_RequiresRequestTenant_KeepsAuditChain() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedBaselineAsync(cs);
        await ExecAsync(cs, InsertRequestSql("r1", "'tenant-a'"));
        (await CountAsync(cs, TenantDefaultCountSql("consents"))).ShouldBe(1, "the v1.5.0 baseline carries the dead default");
        var consents = await CountAsync(cs, $"SELECT COUNT(*) FROM {ConsentsTable}");
        var auditCount = await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries");
        var auditHashes = await QueryAsync(cs, AuditHashesSql);
        var triggers = await CountAsync(cs, TriggerCountSql);

        await RunAsync(cs, GovernanceMigrationMode.Apply);

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 3")).ShouldBe(1);
        (await CountAsync(cs, TenantDefaultCountSql("consents"))).ShouldBe(0);
        (await CountAsync(cs, TenantDefaultCountSql("audit_log_entries"))).ShouldBe(0);
        (await CountAsync(cs, RequestTenantNullableSql)).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM consent_requests WHERE tenant_id = 'tenant-a'")).ShouldBe(1);
        (await CountAsync(cs, $"SELECT COUNT(*) FROM {ConsentsTable}")).ShouldBe(consents);
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "step 0003 does not read or rewrite audit rows");
        (await CountAsync(cs, TriggerCountSql)).ShouldBe(triggers, "the append-only audit triggers stay enabled");
        (await AuditChainVerifiesAsync(cs, [])).ShouldBeTrue("the audit hash chain and anchor verify after step 0003");
        await Should.ThrowAsync<Exception>(() => ExecAsync(cs, InsertRequestSql("r2", "NULL")));

        // A schema past step 0003 without its history row is drift (the nullability is checked strictly), so there is no history-loss rerun here.
        await RunAsync(cs, GovernanceMigrationMode.Verify);
    });

    [Fact]
    [Trait("Category", "Security")]
    public Task I388_SC_06_DT_SC_16_WPDT2b_Step0003_LostHistoryRow_IsDrift_InApplyAndVerify() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedBaselineAsync(cs);
        await RunAsync(cs, GovernanceMigrationMode.Apply, stepVersionCap: 3);
        await ExecAsync(cs, "DELETE FROM autheris_schema_migrations WHERE version = 3");

        var apply = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply, stepVersionCap: 3));
        var verify = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Verify, stepVersionCap: 3));

        apply.Code.ShouldBe(GovernanceMigrationCodes.Drift);
        verify.Code.ShouldBe(GovernanceMigrationCodes.Pending, "Verify refuses a schema whose step 0003 history row is missing");
    });

    [Fact]
    [Trait("Category", "Security")]
    public Task I388_SC_06_DT_SC_16_WPDT2b_Step0003_NullTenantConsentRequest_FailsClosed_AndLeavesTheSchemaAtStep0002() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await SeedBaselineAsync(cs);
        await RunAsync(cs, GovernanceMigrationMode.Apply, stepVersionCap: 2);
        await ExecAsync(cs, InsertRequestSql("r-null", "NULL"));
        var before = await QueryAsync(cs, FingerprintSql);

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply));

        ex.Code.ShouldBe(GovernanceMigrationCodes.StepFailed);
        (await CountAsync(cs, "SELECT MAX(version) FROM autheris_schema_migrations")).ShouldBe(2);
        (await CountAsync(cs, TenantDefaultCountSql("consents"))).ShouldBe(1, "the failed step was rolled back");
        (await CountAsync(cs, RequestTenantNullableSql)).ShouldBe(1);
        (await CountAsync(cs, "SELECT COUNT(*) FROM consent_requests WHERE tenant_id IS NULL")).ShouldBe(1, "no silent backfill");
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);
    });

    [Fact]
    public Task I388_T_05_Adoption_PreV150Database_WithHistory_FailsUnsupportedBase_AndExecutesNoDdl() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db, baselineOnly: true);
        await ExecAsync(cs, DropSomeBaselineTableSql); // an older shape that still carries the 0001 history row
        var before = await QueryAsync(cs, FingerprintSql);
        var trace = new List<string>();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()], trace: trace));

        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty("no DDL and no step may run on an unsupported base");
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before, "the catalog is byte-identical before and after");
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(1);
    });

    [Fact]
    public Task I388_T_05_Adoption_PreV150Database_WithoutHistory_FailsUnsupportedBase_AndExecutesNoDdl() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await ExecAsync(cs, ForeignOldShapeTableSql);
        var before = await QueryAsync(cs, FingerprintSql);
        var trace = new List<string>();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()], trace: trace));

        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty();
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);
        (await CountAsync(cs, HistoryTableCountSql)).ShouldBe(0, "not even the history table is created before the schema is accepted");
    });

    [Fact]
    public Task I388_T_08_ConcurrentRunners_ExactlyOneApplies_OthersVerifyOnly() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var applied = 0;
        var step = ProbeStep(onApply: () => System.Threading.Interlocked.Increment(ref applied));
        var start = new TaskCompletionSource();

        var runs = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await RunAsync(cs, GovernanceMigrationMode.Apply, [step]);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(runs);

        applied.ShouldBe(1);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(5);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(1);
    });

    [Fact]
    public Task I388_T_08_LockTimeout_FailsClosed_NoDdl() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var before = await QueryAsync(cs, FingerprintSql);
        var trace = new List<string>();

        await using (await HoldSchemaLockAsync(cs))
        {
            var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()], lockTimeoutSeconds: 1, trace: trace));

            ex.Code.ShouldBe(GovernanceMigrationCodes.LockTimeout);
            trace.ShouldBeEmpty();

            // Verify takes no lock: it still answers while another migrator holds it.
            await RunAsync(cs, GovernanceMigrationMode.Verify).WaitAsync(TimeSpan.FromSeconds(30));
        }

        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);

        // The lock died with the holder: the very next run applies.
        await RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()]);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(1);
    });

    [Fact]
    public Task I388_T_02_Run_AppliedStepChecksumMismatch_FailsClosed_AppliesNothing() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        await RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()]);
        var before = await QueryAsync(cs, FingerprintSql);
        var trace = new List<string>();

        var edited = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep(seed: "edited")], trace: trace));
        edited.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);

        await ExecAsync(cs, "UPDATE autheris_schema_migrations SET checksum = 'tampered' WHERE version = 1");
        var tampered = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()], trace: trace));
        tampered.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);

        trace.ShouldBeEmpty();
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);
    });

    [Fact]
    public Task I388_T_02_Run_HistoryGapOrRenamedStep_FailsClosedWithOwnCodes() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var trace = new List<string>();

        await ExecAsync(cs, "UPDATE autheris_schema_migrations SET name = 'renamed' WHERE version = 1");
        var renamed = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply, trace: trace));
        renamed.Code.ShouldBe(GovernanceMigrationCodes.HistoryRenamed);
        await ExecAsync(cs, "UPDATE autheris_schema_migrations SET name = 'baseline_governance_schema' WHERE version = 1");

        await ExecAsync(cs, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (6, 'x', 'y', 'z', 'w')");
        var gap = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()], allowNewer: true, trace: trace));
        gap.Code.ShouldBe(GovernanceMigrationCodes.HistoryGap);

        trace.ShouldBeEmpty();
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(0);
    });

    [Fact]
    public Task I388_T_03_Run_NewerThanBuild_FailsClosed_AndAllowNewerStillChecksKnownPrefixWithoutDdl() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var step = ProbeStep();
        await RunAsync(cs, GovernanceMigrationMode.Apply, [step]);
        var trace = new List<string>();

        // An older build (catalogue without step 2) against the newer database.
        var newer = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Apply, trace: trace));
        newer.Code.ShouldBe(GovernanceMigrationCodes.NewerThanBuild);

        var log = new List<string>();
        await RunAsync(cs, GovernanceMigrationMode.Apply, allowNewer: true, trace: trace, log: log);
        trace.ShouldBeEmpty("AllowNewerSchema never executes DDL");
        log.ShouldContain(m => m.Contains(GovernanceMigrationCodes.NewerAllowedWarning, StringComparison.Ordinal));

        await ExecAsync(cs, "UPDATE autheris_schema_migrations SET checksum = 'tampered' WHERE version = 1");
        var tampered = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => RunAsync(cs, GovernanceMigrationMode.Apply, allowNewer: true, trace: trace));
        tampered.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);
        trace.ShouldBeEmpty();
    });

    [Fact]
    public Task I388_T_08_Verify_WithPendingStep_FailsWithPendingCode_AndWritesNothing() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var before = await QueryAsync(cs, FingerprintSql);

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => RunAsync(cs, GovernanceMigrationMode.Verify, [ProbeStep()]));

        ex.Code.ShouldBe(GovernanceMigrationCodes.Pending);
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(4);
    });

    [Fact]
    public Task I388_T_06_Run_DriftInsideStep_AbortsAndRollsBackStepAndRecordsNoVersion() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
        var before = await QueryAsync(cs, FingerprintSql);

        // The delta promises a NOT NULL column, the SQL adds a nullable one: the per-step shape check fails inside the transaction.
        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep(wrongNullability: true)]));

        ex.Code.ShouldBe(GovernanceMigrationCodes.Drift);
        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.WrongNullability && d.Name == ProbeColumn);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 5")).ShouldBe(0);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(0, "the transactional DDL of the step was rolled back");
        (await QueryAsync(cs, FingerprintSql)).ShouldBe(before);

        // Fixing the delta lets the very same database upgrade: nothing was left half-applied.
        await RunAsync(cs, GovernanceMigrationMode.Apply, [ProbeStep()]);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(1);
    });

    [Fact]
    public Task I388_T_11_Verify_RuntimeLoginWithWriteOnHistory_Warns_AndFailsWithRequireLeastPrivilege() => WithDbAsync(async db =>
    {
        await MigrateBaselineAsync(db);
        var (privileged, cleanupPrivileged) = await CreateReadOnlyLoginAsync(db, writeOnHistory: true);
        var (clean, cleanupClean) = await CreateReadOnlyLoginAsync(db, writeOnHistory: false);
        try
        {
            var warned = new List<string>();
            await RunAsync(privileged, GovernanceMigrationMode.Verify, log: warned);
            warned.ShouldContain(m => m.Contains(GovernanceMigrationCodes.RuntimeLoginPrivileged, StringComparison.Ordinal));

            var strict = await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => RunAsync(privileged, GovernanceMigrationMode.Verify, requireLeastPrivilege: true));
            strict.Code.ShouldBe(GovernanceMigrationCodes.RuntimeLoginPrivileged);

            // A login without write access on the history is neither warned about nor rejected, even in strict mode.
            var quiet = new List<string>();
            await RunAsync(clean, GovernanceMigrationMode.Verify, requireLeastPrivilege: true, log: quiet);
            quiet.ShouldNotContain(m => m.Contains(GovernanceMigrationCodes.RuntimeLoginPrivileged, StringComparison.Ordinal));
        }
        finally
        {
            await cleanupPrivileged();
            await cleanupClean();
        }
    });

    protected sealed class CaptureLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
