using System.Globalization;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #388 (WP-388-1): the forward-only migration engine, exercised on SQLite with a test-only step set (no real step exists yet).
/// The same engine code runs on PostgreSQL and SQL Server; their dialect hooks are covered by the integration tests (WP-388-3).
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceMigrationEngineTests
{
    private const string Probe = "PROBE_388";

    private static string NewConnectionString() => $"Data Source=eng_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static SqliteGovernanceRepository NewRepository(string connectionString) =>
        GovernanceStoreFactory.CreateUnmigratedSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = connectionString, SeedDemoData = false } }));

    private static SqliteGovernanceMigrationRunner NewRunner(
        SqliteGovernanceRepository repository,
        string connectionString,
        IReadOnlyList<MigrationStep>? extra = null,
        GovernanceMigrationMode mode = GovernanceMigrationMode.Apply,
        bool allowNewer = false,
        int lockTimeoutSeconds = 300,
        List<string>? trace = null,
        ILogger<SqliteGovernanceMigrationRunner>? logger = null,
        bool production = false,
        bool? runtimeDdl = null,
        Func<string?>? excessPrivilege = null,
        Func<int, Task>? audit = null,
        bool requireLeastPrivilege = false) =>
        new(
            repository,
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions
                {
                    ConnectionString = connectionString,
                    SeedDemoData = false,
                    Migrations = new GovernanceMigrationOptions
                    {
                        Mode = mode,
                        AllowNewerSchema = allowNewer,
                        LockTimeoutSeconds = lockTimeoutSeconds,
                        RequireLeastPrivilege = requireLeastPrivilege
                    }
                }
            }),
            logger)
        {
            AdditionalSteps = extra ?? [],
            Trace = trace is null ? null : trace.Add,
            IsProduction = production,
            RuntimeLoginRunsDdlOverride = runtimeDdl,
            ExcessPrivilegeOverride = excessPrivilege,
            RuntimeDdlAuditOverride = audit
        };

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

    /// <summary>The synthetic step <c>0002_test_add_probe_table</c> of the plan (D-10).</summary>
    private static MigrationStep ProbeStep(
        string sql = $"CREATE TABLE {Probe} (id TEXT NOT NULL PRIMARY KEY, note TEXT NULL);",
        GovernanceSchemaShapeDelta? delta = null,
        string checksumSeed = "v1",
        Action? onApply = null) =>
        new(
            2,
            "test_add_probe_table",
            SqlMigrationStep.ComputeChecksum("sqlite", 2, "test_add_probe_table", checksumSeed + sql),
            ctx =>
            {
                onApply?.Invoke();
                using var cmd = ctx.Connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return Task.CompletedTask;
            },
            Transactional: true)
        {
            ShapeDelta = delta ?? ProbeDelta
        };

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
    public async Task Run_AppliesPendingStep_RecordsVersionNameAndChecksum_AndKeepsExistingData()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        var step = ProbeStep();

        await NewRunner(repository, cs, [step]).RunAsync();

        Scalar(repository, $"SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2 AND name = 'test_add_probe_table' AND checksum = '{step.Checksum}'").ShouldBe(1);
        Scalar(repository, $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{Probe}'").ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1").ShouldBe(1);
    }

    [Fact]
    public async Task Run_StepFails_RollsBackStepAndRecordsNoVersion()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => NewRunner(repository, cs, [ProbeStep(sql: $"CREATE TABLE {Probe} (id TEXT NOT NULL PRIMARY KEY, note TEXT NULL); SELECT * FROM NO_SUCH_TABLE_388;")]).RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.StepFailed);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2").ShouldBe(0);
        Scalar(repository, $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{Probe}'").ShouldBe(0);
    }

    [Fact]
    public async Task Run_StepIsIdempotent_WhenObjectsAlreadyExist()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        var step = ProbeStep(sql: $"CREATE TABLE IF NOT EXISTS {Probe} (id TEXT NOT NULL PRIMARY KEY, note TEXT NULL);");
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, $"CREATE TABLE {Probe} (id TEXT NOT NULL PRIMARY KEY, note TEXT NULL);");

        await NewRunner(repository, cs, [step]).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2").ShouldBe(1);
    }

    [Fact]
    public async Task Run_DriftBeforeAndAfterStep_FailsClosed_AndRollsBack()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();

        // The delta promises a NOT NULL note column, the SQL creates a nullable one (I388-T-06).
        var wrongDelta = new GovernanceSchemaShapeDelta(
        [
            new ShapeOp(
                "addTable",
                Probe,
                NewTable: new SchemaTable(
                    Probe,
                    [new SchemaColumn("id", "TEXT", SchemaTypeFamily.String), new SchemaColumn("note", "TEXT", SchemaTypeFamily.String)],
                    ["id"],
                    []))
        ]);
        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, [ProbeStep(delta: wrongDelta)]).RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.Drift);
        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.WrongNullability && d.Name == "note");
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 2").ShouldBe(0);
        Scalar(repository, $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{Probe}'").ShouldBe(0);

        // Drift before the step: a manual edit of the applied schema is found before any DDL runs.
        Exec(repository, "DROP TABLE CONSENT_ROW_FILTERS;");
        var trace = new List<string>();
        var before = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, [ProbeStep()], trace: trace).RunAsync());
        before.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_AppliedStepChecksumMismatch_FailsClosed_AppliesNothing()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs, [ProbeStep()]).RunAsync();
        var trace = new List<string>();

        var edited = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => NewRunner(repository, cs, [ProbeStep(checksumSeed: "edited")], trace: trace).RunAsync());
        edited.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);

        Exec(repository, "UPDATE autheris_schema_migrations SET checksum = 'tampered' WHERE version = 1;");
        var tampered = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, [ProbeStep()], trace: trace).RunAsync());
        tampered.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);
        trace.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_HistoryGapOrRenamedStep_FailsClosedWithOwnCodes()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        var trace = new List<string>();

        Exec(repository, "UPDATE autheris_schema_migrations SET name = 'renamed' WHERE version = 1;");
        var renamed = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, trace: trace).RunAsync());
        renamed.Code.ShouldBe(GovernanceMigrationCodes.HistoryRenamed);
        Exec(repository, "UPDATE autheris_schema_migrations SET name = 'baseline_governance_schema' WHERE version = 1;");

        Exec(repository, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (3, 'x', 'y', 'z', 'w');");
        var gap = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, allowNewer: true, trace: trace).RunAsync());
        gap.Code.ShouldBe(GovernanceMigrationCodes.HistoryGap);
        trace.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_NewerThanBuild_FailsClosed()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs, [ProbeStep()]).RunAsync();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.NewerThanBuild);
    }

    [Fact]
    public async Task Run_AllowNewerSchema_StillChecksKnownPrefixAndRunsNoDdl()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs, [ProbeStep()]).RunAsync();
        var trace = new List<string>();
        var logger = new ListLogger<SqliteGovernanceMigrationRunner>();

        await NewRunner(repository, cs, allowNewer: true, trace: trace, logger: logger).RunAsync();

        trace.ShouldBeEmpty();
        logger.Messages.ShouldContain(m => m.Contains(GovernanceMigrationCodes.NewerAllowedWarning, StringComparison.Ordinal));

        Exec(repository, "UPDATE autheris_schema_migrations SET checksum = 'tampered' WHERE version = 1;");
        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, allowNewer: true, trace: trace).RunAsync());
        ex.Code.ShouldBe(GovernanceMigrationCodes.ChecksumMismatch);
        trace.ShouldBeEmpty();
    }

    [Fact]
    public async Task Adoption_PreV150Database_FailsUnsupportedBase_AndExecutesNoStatement()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        // A foreign or pre-v1.5.0 schema without history: CONSENTS exists with a different shape.
        Exec(repository, "CREATE TABLE CONSENTS (consent_id TEXT NOT NULL PRIMARY KEY);");
        var trace = new List<string>();

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, [ProbeStep()], trace: trace).RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        trace.ShouldBeEmpty("no DDL and no step may run before the schema is accepted (I388-SC-04)");
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'autheris_schema_migrations'").ShouldBe(0);
        Scalar(repository, "SELECT COUNT(*) FROM pragma_table_info('CONSENTS')").ShouldBe(1);
    }

    [Fact]
    public async Task Adoption_PartialBaselineWithoutHistory_IsCompleted()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "DROP TABLE autheris_schema_migrations;");

        await NewRunner(repository, cs).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations WHERE version = 1").ShouldBe(1);
    }

    [Fact]
    public async Task Verify_WithPendingStep_FailsWithPendingCode_AndWritesNothing()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);

        var fresh = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, mode: GovernanceMigrationMode.Verify).RunAsync());
        fresh.Code.ShouldBe(GovernanceMigrationCodes.Pending);
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master").ShouldBe(0);

        await NewRunner(repository, cs).RunAsync();
        var pending = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => NewRunner(repository, cs, [ProbeStep()], GovernanceMigrationMode.Verify).RunAsync());
        pending.Code.ShouldBe(GovernanceMigrationCodes.Pending);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(1);
    }

    [Fact]
    public async Task Verify_TakesNoLock_AndWritesNothing()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        await repository.LockUnguardedAsync();
        try
        {
            // The write gate is held by "another migrator": a verifier must neither wait for it nor write.
            await NewRunner(repository, cs, mode: GovernanceMigrationMode.Verify, lockTimeoutSeconds: 10).VerifyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            repository.ReleaseLock();
        }
    }

    [Fact]
    public async Task LockTimeout_FailsClosed_NoDdl()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await repository.LockUnguardedAsync();
        var trace = new List<string>();
        try
        {
            var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => NewRunner(repository, cs, [ProbeStep()], lockTimeoutSeconds: 1, trace: trace).RunAsync());

            ex.Code.ShouldBe(GovernanceMigrationCodes.LockTimeout);
            trace.ShouldBeEmpty();
        }
        finally
        {
            repository.ReleaseLock();
        }
    }

    [Fact]
    public async Task ConcurrentRunners_ExactlyOneApplies_OthersVerifyOnly()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        var applied = 0;
        using var barrier = new Barrier(3);
        var step = ProbeStep(onApply: () => Interlocked.Increment(ref applied));

        var runs = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await NewRunner(repository, cs, [step]).RunAsync();
        })).ToArray();
        await Task.WhenAll(runs);

        applied.ShouldBe(1);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(2);
    }

    [Fact]
    public async Task MigrateGovernanceCli_AppliesWhenModeVerify_RuntimeVerifyOnlyAfterwards_AndLogsModeIgnored()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        var logger = new ListLogger<SqliteGovernanceMigrationRunner>();

        await NewRunner(repository, cs, [ProbeStep()], GovernanceMigrationMode.Verify, logger: logger).ApplyAsync();

        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(2);
        logger.Messages.ShouldContain(m => m.Contains("is ignored by the explicit governance migration run", StringComparison.Ordinal));
        await NewRunner(repository, cs, [ProbeStep()], GovernanceMigrationMode.Verify).RunAsync();
    }

    [Fact]
    public async Task StepFailure_ErrorCarriesNoRowValuesSqlTextOrConnectionDetails()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        var step = ProbeStep(sql: $"CREATE TABLE {Probe} (id TEXT NOT NULL PRIMARY KEY, note TEXT NULL); INSERT INTO {Probe} (id) VALUES ('secret-value-388'); INSERT INTO {Probe} (id) VALUES ('secret-value-388');");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, [step]).RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.StepFailed);
        ex.Message.ShouldNotContain("secret-value-388");
        ex.Message.ShouldNotContain("INSERT");
        ex.Message.ShouldNotContain(cs);
        ex.InnerException.ShouldBeNull();
        ex.Message.ShouldContain("SQLite error");
    }

    [Fact]
    public void DriftReport_IsCappedAndHasNoConnectionDetails()
    {
        var deviations = Enumerable.Range(0, 120)
            .Select(i => new SchemaDeviation(SchemaDeviationKind.MissingTable, "T" + i.ToString(CultureInfo.InvariantCulture), null, string.Empty, null))
            .ToArray();

        var message = GovernanceSchemaShapeComparer.FormatMessage("SQLite", "governance.db", [1], [], deviations);

        message.ShouldContain("... and 70 more");
        message.ShouldNotContain("Password", Case.Insensitive);
        message.Split('\n').Count(l => l.Contains("missing table", StringComparison.Ordinal)).ShouldBe(50);
    }

    [Fact]
    public async Task Shape_Nullability_FreshBaseline_NoFalseWrongNullability()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();

        var verifier = new GovernanceSchemaShapeVerifier("sqlite", new SqliteSchemaCatalogReader());
        await using var connection = await repository.Connections.OpenAsync(CancellationToken.None);
        var result = await verifier.VerifyAsync(connection, CancellationToken.None);

        result.Deviations.ShouldBeEmpty();
        verifier.Expected.Tables.SelectMany(t => t.Columns).ShouldContain(c => c.Nullable);
    }

    [Fact]
    public void Shape_WrongNullability_Detected()
    {
        var expected = new GovernanceSchemaShape("sqlite", [new SchemaTable("T", [new SchemaColumn("c", "TEXT", SchemaTypeFamily.String)], [], [])]);
        var actual = new GovernanceSchemaShape("sqlite", [new SchemaTable("T", [new SchemaColumn("c", "TEXT", SchemaTypeFamily.String, Nullable: true)], [], [])]);

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
        var shape = GovernanceSchemaShapeLoader.LoadAt("sqlite", 2, new Dictionary<int, GovernanceSchemaShapeDelta> { [2] = ProbeDelta });

        shape.Tables.ShouldContain(t => t.Name == Probe);
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeLoader.LoadAt("sqlite", 2, new Dictionary<int, GovernanceSchemaShapeDelta>()));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeFolder.Fold(shape, new GovernanceSchemaShapeDelta([new ShapeOp("dropTable", "NO_SUCH")])));
        Should.Throw<InvalidOperationException>(() => GovernanceSchemaShapeFolder.Fold(shape, ProbeDelta));
    }

    [Fact]
    public async Task I388_SC_13_Production_Apply_WithoutMigrationConnectionString_WarnsRuntimeDdl_AndAuditsBestEffort()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        var logger = new ListLogger<SqliteGovernanceMigrationRunner>();
        var audited = new List<int>();
        var runner = NewRunner(repository, cs, [ProbeStep()], production: true, runtimeDdl: true, logger: logger, audit: n => { audited.Add(n); return Task.CompletedTask; });

        await runner.RunAsync();

        logger.Messages.ShouldContain(m => m.Contains(GovernanceMigrationCodes.RuntimeDdl, StringComparison.Ordinal));
        audited.ShouldBeEmpty("the audit event waits until the audit store is ready");
        await runner.EmitPendingAuditAsync();
        audited.ShouldBe([2]);
        await runner.EmitPendingAuditAsync();
        audited.Count.ShouldBe(1);
    }

    [Fact]
    public async Task I388_SC_13_AuditFailure_IsSwallowed_AndNoWarningOutsideProductionOrWithMigrationLogin()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        var runner = NewRunner(repository, cs, [ProbeStep()], production: true, runtimeDdl: true, audit: _ => throw new InvalidOperationException("boom"));
        await runner.RunAsync();
        await runner.EmitPendingAuditAsync();

        foreach (var (production, runtimeDdl) in new[] { (false, true), (true, false) })
        {
            var cs2 = NewConnectionString();
            using var repo2 = NewRepository(cs2);
            var logger = new ListLogger<SqliteGovernanceMigrationRunner>();
            var audited = new List<int>();
            var other = NewRunner(repo2, cs2, [ProbeStep()], production: production, runtimeDdl: runtimeDdl, logger: logger, audit: n => { audited.Add(n); return Task.CompletedTask; });
            await other.RunAsync();
            await other.EmitPendingAuditAsync();
            logger.Messages.ShouldNotContain(m => m.Contains(GovernanceMigrationCodes.RuntimeDdl, StringComparison.Ordinal));
            audited.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Verify_RuntimeLoginPrivileged_WarnsByDefault_AndFailsWithRequireLeastPrivilege()
    {
        var cs = NewConnectionString();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();

        var logger = new ListLogger<SqliteGovernanceMigrationRunner>();
        await NewRunner(repository, cs, mode: GovernanceMigrationMode.Verify, logger: logger, excessPrivilege: () => "write access to autheris_schema_migrations").RunAsync();
        logger.Messages.ShouldContain(m => m.Contains(GovernanceMigrationCodes.RuntimeLoginPrivileged, StringComparison.Ordinal));

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(
            () => NewRunner(repository, cs, mode: GovernanceMigrationMode.Verify, requireLeastPrivilege: true, excessPrivilege: () => "CREATE privilege on the schema").RunAsync());
        ex.Code.ShouldBe(GovernanceMigrationCodes.RuntimeLoginPrivileged);

        await NewRunner(repository, cs, mode: GovernanceMigrationMode.Verify, requireLeastPrivilege: true, excessPrivilege: () => null).RunAsync();
    }

    [Fact]
    public void EveryEmbeddedStep_LoadsAndIsNumberedConsecutively_ForAllEngines()
    {
        foreach (var engine in new[] { "sqlite", "postgresql", "sqlserver" })
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
        foreach (var engine in new[] { "sqlite", "postgresql", "sqlserver" })
        {
            foreach (var step in SqlMigrationStep.LoadEmbedded(engine).Where(s => s.Phase3Marker is not null))
            {
                plans.ShouldContain(step.Phase3Marker!, Case.Sensitive, $"{engine} step {step.Version:D4} cites an unknown phase-3 condition");
            }
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
