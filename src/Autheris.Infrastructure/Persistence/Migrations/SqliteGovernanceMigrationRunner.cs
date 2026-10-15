using System.Data.Common;
using Autheris.Domain.Options;
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
            })
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
