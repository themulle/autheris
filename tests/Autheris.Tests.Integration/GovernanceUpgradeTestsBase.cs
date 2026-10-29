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
        bool baselineOnly = false);

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
            3,
            StepName,
            SqlMigrationStep.ComputeChecksum(Engine, 3, StepName, seed + sql),
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
    private async Task MigrateBaselineAsync(TestDb db) => await RunAsync(db.ConnectionString, GovernanceMigrationMode.Apply, baselineOnly: true);

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

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 3 AND name = '" + StepName + "' AND checksum = '" + step.Checksum + "'")).ShouldBe(1);
        (await CountAsync(cs, ProbeColumnCountSql)).ShouldBe(1);
        (await CountAsync(cs, $"SELECT COUNT(*) FROM {ConsentsTable}")).ShouldBe(consents);
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "no step may read or rewrite audit rows");
        (await CountAsync(cs, TriggerCountSql)).ShouldBe(triggersBefore, "the append-only audit triggers stay enabled");
        (await AuditChainVerifiesAsync(cs, [step])).ShouldBeTrue("the audit hash chain and anchor verify after the upgrade");

        // A second run and a Verify run are no-ops on the upgraded database.
        await RunAsync(cs, GovernanceMigrationMode.Apply, [step]);
        await RunAsync(cs, GovernanceMigrationMode.Verify, [step]);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(3);
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

        await RunAsync(cs, GovernanceMigrationMode.Apply);

        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2 AND name = 'key_and_index_column_direction' AND checksum = '" + checksum + "'")).ShouldBe(1);
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_primary_key_columns")).ShouldBe(2);
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_primary_key_columns")).ShouldBe(0, "existing key rows read as ascending");
        (await CountAsync(cs, "SELECT COUNT(*) FROM table_index_columns")).ShouldBe(2);
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_index_columns")).ShouldBe(0);
        (await CountAsync(cs, "SELECT COUNT(*) FROM audit_log_entries")).ShouldBe(auditCount);
        (await QueryAsync(cs, AuditHashesSql)).ShouldBe(auditHashes, "step 0002 does not read or rewrite audit rows");
        (await CountAsync(cs, TriggerCountSql)).ShouldBeGreaterThan(0, "the append-only audit triggers stay enabled");
        (await AuditChainVerifiesAsync(cs, [])).ShouldBeTrue("the audit hash chain and anchor verify after step 0002");

        // A v1.5.0 binary (AllowNewerSchema rollback) inserts without the column and reads ascending.
        await ExecAsync(cs, "INSERT INTO table_primary_key_columns (table_id, key_ordinal, table_column_id) VALUES ('t390b', 1, 'c9')");
        (await CountAsync(cs, "SELECT SUM(CAST(is_descending AS INTEGER)) FROM table_primary_key_columns WHERE table_id = 't390b'")).ShouldBe(0);

        // Rerun after the history row is lost: the guarded statements are idempotent and the shape still verifies.
        await ExecAsync(cs, "DELETE FROM autheris_schema_migrations WHERE version = 2");
        await RunAsync(cs, GovernanceMigrationMode.Apply);
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2")).ShouldBe(1);
        await RunAsync(cs, GovernanceMigrationMode.Verify);
    });

    [Fact]
    public Task I388_T_05_Adoption_PreV150Database_WithHistory_FailsUnsupportedBase_AndExecutesNoDdl() => WithDbAsync(async db =>
    {
        var cs = db.ConnectionString;
        await MigrateBaselineAsync(db);
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
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(3);
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

        await ExecAsync(cs, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (3, 'x', 'y', 'z', 'w')");
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
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations")).ShouldBe(1);
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
        (await CountAsync(cs, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 3")).ShouldBe(0);
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
