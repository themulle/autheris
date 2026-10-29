using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Migration catalogue and runner for the PostgreSQL governance store. Honors <c>GovernanceDb:MigrationConnectionString</c> (separate DDL role).</summary>
public sealed class PostgreSqlGovernanceMigrationRunner : GovernanceMigrationRunnerBase
{
    /// <summary>Session-level advisory lock that serialises the schema DDL of concurrently starting replicas ("AUTH" "SCHE").</summary>
    private const long SchemaAdvisoryLockKey = 0x41555448_53434845L;

    private static readonly IReadOnlyList<MigrationStep> Catalogue =
    [
        new MigrationStep(
            1,
            "baseline_governance_schema",
            MigrationStep.ComputeChecksum("postgresql:0001:baseline_governance_schema:v1"),
            ctx =>
            {
                PostgreSqlBaselineSchema.Apply((NpgsqlConnection)ctx.Connection, ctx.Logger);
                return Task.CompletedTask;
            }),
        .. SqlMigrationStep.LoadEmbedded("postgresql").Select(step => step.ToMigrationStep())
    ];

    private static readonly GovernanceSchemaShapeVerifier Verifier = new("postgresql", new PostgreSqlSchemaCatalogReader());

    private readonly PostgreSqlGovernanceRepository _repository;

    public PostgreSqlGovernanceMigrationRunner(
        PostgreSqlGovernanceRepository repository,
        IOptions<GatewayOptions> options,
        ILogger<PostgreSqlGovernanceMigrationRunner>? logger = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
        : base(SqliteGovernanceMigrationRunner.GetMigrationOptions(options), logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        IsProduction = environment is not null && Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsProduction(environment);
    }

    protected override bool RuntimeLoginRunsDdl => string.IsNullOrWhiteSpace(_repository.MigrationConnectionString);

    protected override async Task<string?> DescribeExcessPrivilegeAsync(DbConnection connection, CancellationToken ct)
    {
        // has_table_privilege / has_schema_privilege: read-only introspection of the effective privileges (role inheritance included).
        var writeHistory = await ScalarAsync(
            connection,
            "SELECT CASE WHEN to_regclass('autheris_schema_migrations') IS NOT NULL AND has_table_privilege(current_user, 'autheris_schema_migrations', 'INSERT,UPDATE,DELETE,TRUNCATE') THEN 1 ELSE 0 END",
            ct).ConfigureAwait(false);
        if (Convert.ToInt32(writeHistory, CultureInfo.InvariantCulture) == 1)
        {
            return "write access to autheris_schema_migrations";
        }

        var createSchema = await ScalarAsync(
            connection, "SELECT CASE WHEN has_schema_privilege(current_user, current_schema(), 'CREATE') THEN 1 ELSE 0 END", ct).ConfigureAwait(false);
        return Convert.ToInt32(createSchema, CultureInfo.InvariantCulture) == 1 ? "CREATE privilege on the schema" : null;
    }

    protected override Task RecordRuntimeDdlAuditAsync(int appliedSteps, CancellationToken ct) =>
        _repository.RecordAuditEventAsync(RuntimeDdlAudit.Build(appliedSteps), ct);

    public override IReadOnlyList<MigrationStep> Steps => Catalogue;

    internal override GovernanceSchemaShapeVerifier ShapeVerifier => Verifier;

    protected override string EngineDisplayName => "PostgreSQL";

    protected override string CreateVersionTableSql =>
        "CREATE TABLE IF NOT EXISTS autheris_schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL, applied_by TEXT NOT NULL)";

    protected override string SelectVersionsSql => "SELECT version, checksum, name FROM autheris_schema_migrations ORDER BY version";

    protected override string HistoryTableExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'autheris_schema_migrations'";

    protected override string InsertVersionSql =>
        "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by)";

    protected override async Task<DbConnection> OpenConnectionAsync(bool verifyOnly, CancellationToken ct)
    {
        // Mode=Verify only reads: it runs with the runtime login, never with the DDL login (least privilege, plan D-12).
        if (verifyOnly || string.IsNullOrWhiteSpace(_repository.MigrationConnectionString))
        {
            return await _repository.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        // Review PG-7: DDL with a separate migration role when configured (the runtime role then needs no DDL rights).
        var conn = new NpgsqlConnection(_repository.MigrationConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Polls <c>pg_try_advisory_lock</c> with a jittered back-off until the deadline (I388-SC-09): a blocked acquisition never hangs.</summary>
    protected override async Task AcquireLockAsync(DbConnection connection, TimeSpan timeout, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sql = "SELECT pg_try_advisory_lock(" + SchemaAdvisoryLockKey.ToString(CultureInfo.InvariantCulture) + ")";
        while (true)
        {
            if (Convert.ToBoolean(await ScalarAsync(connection, sql, ct).ConfigureAwait(false), CultureInfo.InvariantCulture))
            {
                return;
            }

            if (clock.Elapsed >= timeout)
            {
                throw LockTimeoutError();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>I388-SC-08: <c>SET LOCAL</c> lock and statement timeouts of the step transaction.</summary>
    protected override Task BeginStepBoundsAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "SET LOCAL lock_timeout = " + (Options.StepLockTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture) +
            "; SET LOCAL statement_timeout = " + (Options.StepStatementTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture),
            ct);

    protected override Task ReleaseLockAsync(DbConnection connection, bool success, CancellationToken ct) =>
        ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(" + SchemaAdvisoryLockKey.ToString(CultureInfo.InvariantCulture) + ")", ct);

    /// <summary>
    /// SEC-94-09: both append-only triggers must sit on <c>audit_log_entries</c> as resolved through the <c>search_path</c>, call
    /// <c>autheris_audit_append_only</c>, have the expected timing and events and must not be disabled (or replica-only).
    /// </summary>
    internal const string AuditTriggerPostConditionSql =
        "SELECT COUNT(*) FROM pg_trigger t WHERE NOT t.tgisinternal " +
        "AND t.tgrelid = to_regclass('audit_log_entries') " +
        "AND t.tgenabled IN ('O', 'A') " +
        "AND t.tgfoid = to_regproc('autheris_audit_append_only') " +
        "AND ((t.tgname = 'trg_audit_append_only_row' AND (t.tgtype & 1) = 1 AND (t.tgtype & 2) = 2 AND (t.tgtype & 8) = 8 AND (t.tgtype & 16) = 16 AND (t.tgtype & 32) = 0) " +
        "OR (t.tgname = 'trg_audit_append_only_trunc' AND (t.tgtype & 1) = 0 AND (t.tgtype & 2) = 2 AND (t.tgtype & 32) = 32 AND (t.tgtype & 8) = 0 AND (t.tgtype & 16) = 0))";

    protected override async Task VerifyPostConditionsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        var count = Convert.ToInt64(await ScalarAsync(connection, AuditTriggerPostConditionSql, ct, transaction).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count != 2)
        {
            throw new GovernanceSchemaDriftException(
                "The append-only audit triggers on AUDIT_LOG_ENTRIES are missing, disabled or misplaced (fail closed).");
        }
    }
}
