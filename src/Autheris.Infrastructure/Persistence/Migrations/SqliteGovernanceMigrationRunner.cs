using System.Data.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Migration catalogue and runner for the SQLite governance store. The whole run is a single <c>BEGIN IMMEDIATE</c> transaction.</summary>
public sealed class SqliteGovernanceMigrationRunner : GovernanceMigrationRunnerBase
{
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
        .. SqlMigrationStep.LoadEmbedded("sqlite").Select(step => step.ToMigrationStep())
    ];

    private static readonly GovernanceSchemaShapeVerifier Verifier = new("sqlite", new SqliteSchemaCatalogReader());

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

    internal override GovernanceSchemaShapeVerifier ShapeVerifier => Verifier;

    protected override string EngineDisplayName => "SQLite";

    protected override string DatabaseLabel(DbConnection connection) => SanitizeLabel(Path.GetFileName(connection.DataSource));

    protected override string CreateVersionTableSql =>
        "CREATE TABLE IF NOT EXISTS autheris_schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL, applied_by TEXT NOT NULL);";

    protected override string SelectVersionsSql => "SELECT version, checksum, name FROM autheris_schema_migrations ORDER BY version;";

    protected override string HistoryTableExistsSql =>
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'autheris_schema_migrations';";

    protected override string InsertVersionSql =>
        "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by);";

    // The run opens its own pooled connection (BEGIN IMMEDIATE) and in-process exclusion goes through the repository's write gate.
    protected override bool OwnsConnection => true;

    protected override bool UseStepTransactions => false;

    protected override async Task<DbConnection> OpenConnectionAsync(bool verifyOnly, CancellationToken ct) =>
        await _repository.Connections.OpenAsync(ct).ConfigureAwait(false);

    // Verify mode never reaches this method: it takes no lock and writes nothing (I388-SC-09).
    protected override async Task AcquireLockAsync(DbConnection connection, TimeSpan timeout, CancellationToken ct)
    {
        await _repository.LockUnguardedAsync(ct).ConfigureAwait(false);
        try
        {
            // The command timeout is the SQLite busy timeout: another process holding the write lock ends in a bounded wait.
            await ExecuteAsync(connection, null, "BEGIN IMMEDIATE;", ct, (int)Math.Max(1, Math.Ceiling(timeout.TotalSeconds))).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            _repository.ReleaseLock();
            throw LockTimeoutError();
        }
        catch
        {
            _repository.ReleaseLock();
            throw;
        }
    }

    /// <summary>I388-SC-08: a rebuild-style step must leave no orphaned foreign key row.</summary>
    protected override async Task AfterStepAsync(DbConnection connection, DbTransaction? transaction, MigrationStep step, CancellationToken ct)
    {
        if (step.Version < 2)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PRAGMA foreign_key_check reported violations after the step.");
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
