using System.Data.Common;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Migration catalogue and runner for the SQLite governance store. The whole run is a single <c>BEGIN IMMEDIATE</c> transaction.</summary>
public sealed class SqliteGovernanceMigrationRunner : GovernanceMigrationRunnerBase
{
    /// <summary>Durable, fenced CDC watermarks (WP-ARC-A-11).</summary>
    private const string CdcWatermarksDdl = @"CREATE TABLE IF NOT EXISTS autheris_cdc_watermarks (source TEXT NOT NULL, domain_name TEXT NOT NULL, schema_name TEXT NOT NULL, table_name TEXT NOT NULL, watermark INTEGER NOT NULL, fencing_token INTEGER NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL, PRIMARY KEY (source, domain_name, schema_name, table_name));";

    private static readonly IReadOnlyList<MigrationStep> Catalogue =
    [
        new MigrationStep(
            1,
            "baseline_governance_schema",
            MigrationStep.ComputeChecksum("sqlite:0001:baseline_governance_schema:v1"),
            ctx =>
            {
                new SqliteBaselineSchema((SqliteConnection)ctx.Connection, ctx.Logger).Apply();
                return Task.CompletedTask;
            }),
        new MigrationStep(
            2,
            "webhook_outbox",
            MigrationStep.ComputeChecksum("sqlite:0002:webhook_outbox:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, WebhookOutboxSchema.Sqlite, ctx.CancellationToken),
            Transactional: true),
        new MigrationStep(
            3,
            "cdc_watermarks",
            MigrationStep.ComputeChecksum("sqlite:0003:cdc_watermarks:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, CdcWatermarksDdl, ctx.CancellationToken),
            Transactional: true),
        new MigrationStep(
            4,
            "cloudevent_subscriptions",
            MigrationStep.ComputeChecksum("sqlite:0004:cloudevent_subscriptions:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, CloudEventSubscriptionsSchema.Sqlite, ctx.CancellationToken),
            Transactional: true)
    ];

    private readonly SqliteGovernanceRepository _repository;

    public SqliteGovernanceMigrationRunner(
        SqliteGovernanceRepository repository,
        IOptions<GatewayOptions> options,
        ILogger<SqliteGovernanceMigrationRunner>? logger = null)
        : base(GetMigrationOptions(options), logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public override IReadOnlyList<MigrationStep> Steps => Catalogue;

    protected override string CreateVersionTableSql =>
        "CREATE TABLE IF NOT EXISTS autheris_schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL, applied_by TEXT NOT NULL);";

    protected override string SelectVersionsSql => "SELECT version, checksum FROM autheris_schema_migrations ORDER BY version;";

    protected override string InsertVersionSql =>
        "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by);";

    // The run opens its own pooled connection (BEGIN IMMEDIATE) and in-process exclusion goes through the repository's write gate.
    protected override bool OwnsConnection => true;

    protected override bool UseStepTransactions => false;

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct) =>
        await _repository.Connections.OpenAsync(ct).ConfigureAwait(false);

    protected override async Task AcquireLockAsync(DbConnection connection, CancellationToken ct)
    {
        await _repository.LockUnguardedAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
        }
        catch
        {
            _repository.ReleaseLock();
            throw;
        }
    }

    protected override async Task ReleaseLockAsync(DbConnection connection, bool success, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, null, success ? "COMMIT;" : "ROLLBACK;", ct).ConfigureAwait(false);
        }
        finally
        {
            _repository.ReleaseLock();
        }
    }

    internal static GovernanceMigrationOptions GetMigrationOptions(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Value?.GovernanceDb?.Migrations ?? new GovernanceMigrationOptions();
    }
}
