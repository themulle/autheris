namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #388 (WP-388-3) on PostgreSQL: see <see cref="GovernanceUpgradeTestsBase"/>.</summary>
[Trait("Category", "Security")]
public sealed class PostgreSqlGovernanceUpgradeTests : GovernanceUpgradeTestsBase
{
    private const long SchemaAdvisoryLockKey = 0x41555448_53434845L;
    private const string Password = "Lp-test-9f3a!";

    protected override string Engine => "postgresql";
    protected override string ConsentsTable => "consents";
    protected override string ProbeColumnType => "TEXT";
    protected override string AddProbeColumnSql => "ALTER TABLE consents ADD COLUMN IF NOT EXISTS probe_388 TEXT NULL";
    protected override string ProbeColumnCountSql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'consents' AND column_name = 'probe_388'";
    protected override string TriggerCountSql => "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = 'audit_log_entries'::regclass AND NOT tgisinternal AND tgenabled = 'O'";
    protected override string TenantDefaultCountSql(string table) => $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = 'tenant_id' AND column_default IS NOT NULL";
    protected override string RequestTenantNullableSql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'consent_requests' AND column_name = 'tenant_id' AND is_nullable = 'YES'";
    protected override string AuditHashesSql => "SELECT md5(string_agg(entry_hash, ',' ORDER BY seq)) FROM audit_log_entries";
    protected override string HistoryTableCountSql => "SELECT COUNT(*) FROM pg_class WHERE relname = 'autheris_schema_migrations'";
    protected override string DropSomeBaselineTableSql => "DROP TABLE consumer_app_daily";
    protected override string ForeignOldShapeTableSql => "CREATE TABLE consent_column_rules (id TEXT PRIMARY KEY)";

    protected override string FingerprintSql =>
        "SELECT (SELECT md5(coalesce(string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY table_name, ordinal_position), '')) FROM information_schema.columns WHERE table_schema = 'public')"
        + " || ':' || (SELECT md5(coalesce(string_agg(indexdef, ',' ORDER BY indexname), '')) FROM pg_indexes WHERE schemaname = 'public')"
        + " || ':' || (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal)"
        + " || ':' || (SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace)";

    private static IOptions<GatewayOptions> Opts(string cs, GovernanceMigrationMode mode, bool allowNewer, int lockTimeoutSeconds, bool requireLeastPrivilege) =>
        Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = cs,
                SeedDemoData = false,
                Migrations = new GovernanceMigrationOptions
                {
                    Mode = mode,
                    AllowNewerSchema = allowNewer,
                    LockTimeoutSeconds = lockTimeoutSeconds,
                    RequireLeastPrivilege = requireLeastPrivilege
                }
            }
        });

    protected override async Task<TestDb?> NewDbAsync()
    {
        var db = await PostgreSqlTestDatabase.CreateAsync();
        return db.IsAvailable ? new TestDb(db.ConnectionString, db.DisposeAsync) : null;
    }

    protected override async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<string> QueryAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    protected override async Task RunAsync(
        string connectionString,
        GovernanceMigrationMode mode,
        IReadOnlyList<MigrationStep>? extra = null,
        bool allowNewer = false,
        int lockTimeoutSeconds = 300,
        bool requireLeastPrivilege = false,
        List<string>? log = null,
        List<string>? trace = null,
        bool baselineOnly = false,
        int? stepVersionCap = null)
    {
        var options = Opts(connectionString, mode, allowNewer, lockTimeoutSeconds, requireLeastPrivilege);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        var runner = new PostgreSqlGovernanceMigrationRunner(
            repository, options, log is null ? null : new CaptureLogger<PostgreSqlGovernanceMigrationRunner>(log))
        {
            AdditionalSteps = extra ?? [],
            StepVersionCapOverride = stepVersionCap ?? (baselineOnly ? 1 : null),
            Trace = trace is null ? null : s => { lock (trace) { trace.Add(s); } }
        };
        await runner.RunAsync();
    }

    protected override async Task SeedV150Async(string connectionString)
    {
        await using var repository = GovernanceStoreFactory.CreatePostgreSql(
            Substitute.For<IEpochValidationService>(), Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false));
        await SeedCoreAsync(repository, () => repository.FlushAuditChannelAsync());
    }

    protected override async Task SeedBaselineAsync(string connectionString)
    {
        var options = Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = 1 }.RunAsync();
        await repository.InitializeAsync();
        for (var i = 0; i < 25; i++)
        {
            await repository.RecordAuditEventAsync(new Autheris.Domain.Model.AuditLogEntry
            {
                TenantId = "tenant-390",
                EventType = "UPGRADE_390",
                ActorSid = new Autheris.Domain.Common.Sid("S-1-390-" + i),
                TargetTable = "dbo.t",
                TraceId = Guid.NewGuid().ToString("N")
            });
        }

        await repository.FlushAuditChannelAsync();
        if (!await repository.VerifyAuditHashChainAsync())
        {
            throw new InvalidOperationException("The seeded v1.5.0 audit chain must verify before the upgrade.");
        }
    }

    protected override async Task<bool> AuditChainVerifiesAsync(string connectionString, IReadOnlyList<MigrationStep> extra)
    {
        var options = Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options) { AdditionalSteps = extra }.RunAsync();
        await repository.InitializeAsync();
        return await repository.VerifyAuditHashChainAsync();
    }

    protected override async Task<IAsyncDisposable> HoldSchemaLockAsync(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_advisory_lock(" + SchemaAdvisoryLockKey.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            await cmd.ExecuteNonQueryAsync();
        }

        return conn; // disposing the session releases the advisory lock
    }

    protected override async Task<(string ConnectionString, Func<Task> Cleanup)> CreateReadOnlyLoginAsync(TestDb db, bool writeOnHistory)
    {
        var role = "autheris_up_" + Guid.NewGuid().ToString("N")[..12];
        var database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database;
        await ExecAsync(db.ConnectionString, $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;");
        await ExecAsync(db.ConnectionString, $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\";");
        if (writeOnHistory)
        {
            await ExecAsync(db.ConnectionString, $"GRANT INSERT ON autheris_schema_migrations TO \"{role}\";");
        }

        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = Password, Pooling = false }.ConnectionString;
        return (cs, async () =>
        {
            try
            {
                await ExecAsync(db.ConnectionString, $"DROP OWNED BY \"{role}\"; DROP ROLE IF EXISTS \"{role}\";");
            }
            catch (PostgresException)
            {
                // best effort: the shared server is discarded with the run
            }
        });
    }

    private PostgreSqlGovernanceMigrationRunner EngineRunner(
        PostgreSqlGovernanceRepository repository,
        string cs,
        int lockTimeoutSeconds = 300,
        List<string>? log = null,
        bool production = false,
        bool? runtimeDdl = null,
        Func<int, Task>? audit = null,
        Func<DbConnection, TimeSpan, CancellationToken, Task>? acquireLock = null) =>
        new(
            repository,
            Opts(cs, GovernanceMigrationMode.Apply, false, lockTimeoutSeconds, false),
            log is null ? null : new CaptureLogger<PostgreSqlGovernanceMigrationRunner>(log))
        {
            AdditionalSteps = [ProbeStep()],
            IsProduction = production,
            RuntimeLoginRunsDdlOverride = runtimeDdl,
            RuntimeDdlAuditOverride = audit,
            AcquireLockOverride = acquireLock
        };

    private static async Task WithEngineAsync(Func<PostgreSqlGovernanceUpgradeTests, string, PostgreSqlGovernanceRepository, Task> body, PostgreSqlGovernanceUpgradeTests self)
    {
        var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        try
        {
            using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Opts(db.ConnectionString, GovernanceMigrationMode.Apply, false, 300, false));
            await body(self, db.ConnectionString, repository);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public Task LockTimeout_ClientTokenFiresFirst_ProviderExceptionIsMappedToLockTimeout() => WithEngineAsync(async (self, cs, repository) =>
    {
        // The client driver aborts the command with a provider exception (not an OperationCanceledException) when the client-side token fires first.
        var runner = self.EngineRunner(repository, cs, lockTimeoutSeconds: 1, acquireLock: async (_, _, token) =>
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = token.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            throw new InvalidOperationException("Operation cancelled by user.");
        });

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => runner.RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.LockTimeout);
    }, this);

    [Fact]
    public Task LockAcquisition_DriftExceptionFromProvider_PassesThroughUnchanged() => WithEngineAsync(async (self, cs, repository) =>
    {
        var runner = self.EngineRunner(repository, cs, acquireLock: (_, _, _) =>
            throw new GovernanceSchemaDriftException(GovernanceMigrationCodes.LockFailed, "lock failed"));

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => runner.RunAsync());

        ex.Code.ShouldBe(GovernanceMigrationCodes.LockFailed);
    }, this);

    [Fact]
    public Task LockAcquisition_CallerCancellation_SurfacesAsOperationCanceled() => WithEngineAsync(async (self, cs, repository) =>
    {
        using var cts = new CancellationTokenSource();
        var runner = self.EngineRunner(repository, cs, acquireLock: async (_, _, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        });

        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(cts.Token));
    }, this);

    [Fact]
    public Task I388_SC_13_Production_Apply_WithoutMigrationConnectionString_WarnsRuntimeDdl_AndAuditsBestEffort() => WithEngineAsync(async (self, cs, repository) =>
    {
        var log = new List<string>();
        var audited = new List<int>();
        var runner = self.EngineRunner(repository, cs, log: log, production: true, runtimeDdl: true, audit: n => { audited.Add(n); return Task.CompletedTask; });

        await runner.RunAsync();

        log.ShouldContain(m => m.Contains(GovernanceMigrationCodes.RuntimeDdl, StringComparison.Ordinal));
        audited.ShouldBeEmpty("the audit event waits until the audit store is ready");
        await runner.EmitPendingAuditAsync();
        audited.Count.ShouldBe(1);
        await runner.EmitPendingAuditAsync();
        audited.Count.ShouldBe(1);
    }, this);

    [Fact]
    public Task I388_SC_13_AuditFailure_IsSwallowed_AndNoWarningOutsideProductionOrWithMigrationLogin() => WithEngineAsync(async (self, cs, repository) =>
    {
        var runner = self.EngineRunner(repository, cs, production: true, runtimeDdl: true, audit: _ => throw new InvalidOperationException("boom"));
        await runner.RunAsync();
        await runner.EmitPendingAuditAsync(); // swallowed: must not throw

        foreach (var (production, runtimeDdl) in new[] { (false, true), (true, false) })
        {
            var db2 = await PostgreSqlTestDatabase.CreateAsync();
            try
            {
                using var repo2 = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Opts(db2.ConnectionString, GovernanceMigrationMode.Apply, false, 300, false));
                var log = new List<string>();
                var audited = new List<int>();
                var other = self.EngineRunner(repo2, db2.ConnectionString, log: log, production: production, runtimeDdl: runtimeDdl, audit: n => { audited.Add(n); return Task.CompletedTask; });
                await other.RunAsync();
                await other.EmitPendingAuditAsync();
                log.ShouldNotContain(m => m.Contains(GovernanceMigrationCodes.RuntimeDdl, StringComparison.Ordinal));
                audited.ShouldBeEmpty();
            }
            finally
            {
                await db2.DisposeAsync();
            }
        }
    }, this);
}
