namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
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
                EnableOutboxProcessor = false,
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
        bool fullCatalogue = false)
    {
        var options = Opts(connectionString, mode, allowNewer, lockTimeoutSeconds, requireLeastPrivilege);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        var runner = new PostgreSqlGovernanceMigrationRunner(
            repository, options, log is null ? null : new CaptureLogger<PostgreSqlGovernanceMigrationRunner>(log))
        {
            AdditionalSteps = extra ?? [],
            StepVersionCapOverride = fullCatalogue ? null : 1,
            Trace = trace is null ? null : s => { lock (trace) { trace.Add(s); } }
        };
        await runner.RunAsync();
    }

    protected override async Task SeedV150Async(string connectionString)
    {
        var options = Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options) { StepVersionCapOverride = 1 }.RunAsync();
        await repository.InitializeAsync();
        await SeedCoreAsync(repository, () => repository.FlushAuditChannelAsync());
    }

    protected override async Task<bool> AuditChainVerifiesAsync(string connectionString, IReadOnlyList<MigrationStep> extra, bool fullCatalogue = false)
    {
        var options = Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options) { AdditionalSteps = extra, StepVersionCapOverride = fullCatalogue ? null : 1 }.RunAsync();
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
}
