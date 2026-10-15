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
            })
    ];

    private static readonly GovernanceSchemaShapeVerifier Verifier = new("postgresql", new PostgreSqlSchemaCatalogReader());

    private readonly PostgreSqlGovernanceRepository _repository;

    public PostgreSqlGovernanceMigrationRunner(
        PostgreSqlGovernanceRepository repository,
        IOptions<GatewayOptions> options,
        ILogger<PostgreSqlGovernanceMigrationRunner>? logger = null)
        : base(SqliteGovernanceMigrationRunner.GetMigrationOptions(options), logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public override IReadOnlyList<MigrationStep> Steps => Catalogue;

    internal override GovernanceSchemaShapeVerifier ShapeVerifier => Verifier;

    protected override string EngineDisplayName => "PostgreSQL";

    protected override string CreateVersionTableSql =>
        "CREATE TABLE IF NOT EXISTS autheris_schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL, applied_by TEXT NOT NULL)";

    protected override string SelectVersionsSql => "SELECT version, checksum FROM autheris_schema_migrations ORDER BY version";

    protected override string InsertVersionSql =>
        "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by)";

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        // Mode=Verify only reads: it runs with the runtime login, never with the DDL login (least privilege, plan D-12).
        if (Mode == GovernanceMigrationMode.Verify || string.IsNullOrWhiteSpace(_repository.MigrationConnectionString))
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

    protected override Task AcquireLockAsync(DbConnection connection, CancellationToken ct) =>
        ExecuteAsync(connection, null, "SELECT pg_advisory_lock(" + SchemaAdvisoryLockKey.ToString(CultureInfo.InvariantCulture) + ")", ct);

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

    protected override async Task VerifyPostConditionsAsync(DbConnection connection, CancellationToken ct)
    {
        var count = Convert.ToInt64(await ScalarAsync(connection, AuditTriggerPostConditionSql, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count != 2)
        {
            throw new GovernanceSchemaDriftException(
                "The append-only audit triggers on AUDIT_LOG_ENTRIES are missing, disabled or misplaced (fail closed).");
        }
    }
}
