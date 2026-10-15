using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Migration catalogue and runner for the SQL Server governance store. Honors <c>GovernanceDb:MigrationConnectionString</c> (separate DDL login).</summary>
public sealed class SqlServerGovernanceMigrationRunner : GovernanceMigrationRunnerBase
{
    /// <summary>Application lock resource that serialises the schema DDL of concurrently starting replicas.</summary>
    private const string SchemaAppLockResource = "autheris:governance:schema";

    private static readonly IReadOnlyList<MigrationStep> Catalogue =
    [
        new MigrationStep(
            1,
            "baseline_governance_schema",
            MigrationStep.ComputeChecksum("sqlserver:0001:baseline_governance_schema:v1"),
            ctx =>
            {
                SqlServerBaselineSchema.Apply((SqlConnection)ctx.Connection);
                return Task.CompletedTask;
            })
    ];

    private static readonly GovernanceSchemaShapeVerifier Verifier = new("sqlserver", new SqlServerSchemaCatalogReader());

    private readonly SqlServerGovernanceRepository _repository;

    public SqlServerGovernanceMigrationRunner(
        SqlServerGovernanceRepository repository,
        IOptions<GatewayOptions> options,
        ILogger<SqlServerGovernanceMigrationRunner>? logger = null)
        : base(SqliteGovernanceMigrationRunner.GetMigrationOptions(options), logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public override IReadOnlyList<MigrationStep> Steps => Catalogue;

    internal override GovernanceSchemaShapeVerifier ShapeVerifier => Verifier;

    protected override string EngineDisplayName => "SQL Server";

    protected override string CreateVersionTableSql =>
        "IF OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NULL CREATE TABLE dbo.autheris_schema_migrations (version INT NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL, checksum NVARCHAR(128) NOT NULL, applied_at NVARCHAR(64) NOT NULL, applied_by NVARCHAR(256) NOT NULL)";

    protected override string SelectVersionsSql => "SELECT version, checksum FROM dbo.autheris_schema_migrations ORDER BY version";

    protected override string InsertVersionSql =>
        "INSERT INTO dbo.autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by)";

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        // Mode=Verify only reads: it runs with the runtime login, never with the DDL login (least privilege, plan D-12).
        var connectionString = Mode == GovernanceMigrationMode.Verify || string.IsNullOrWhiteSpace(_repository.MigrationConnectionString)
            ? _repository.ConnectionString
            : _repository.MigrationConnectionString;
        var conn = new SqlConnection(connectionString);
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

    protected override async Task AcquireLockAsync(DbConnection connection, CancellationToken ct)
    {
        await using var lockCmd = connection.CreateCommand();
        lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; SELECT @r;";
        var p = lockCmd.CreateParameter();
        p.ParameterName = "@res";
        p.Value = SchemaAppLockResource;
        lockCmd.Parameters.Add(p);
        var rc = Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (rc < 0)
        {
            throw new InvalidOperationException($"Could not acquire the SQL Server schema application lock (sp_getapplock returned {rc}).");
        }
    }

    protected override async Task ReleaseLockAsync(DbConnection connection, bool success, CancellationToken ct)
    {
        await using var unlockCmd = connection.CreateCommand();
        unlockCmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
        var p = unlockCmd.CreateParameter();
        p.ParameterName = "@res";
        p.Value = SchemaAppLockResource;
        unlockCmd.Parameters.Add(p);
        await unlockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>SEC-94-09: the INSTEAD OF UPDATE/DELETE trigger exists, is enabled and covers both events.</summary>
    internal const string AuditTriggerPostConditionSql =
        "SELECT COUNT(*) FROM sys.triggers tr WHERE tr.name = N'trg_audit_append_only' AND tr.parent_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES') " +
        "AND tr.is_disabled = 0 AND tr.is_instead_of_trigger = 1 " +
        "AND (SELECT COUNT(DISTINCT te.type) FROM sys.trigger_events te WHERE te.object_id = tr.object_id AND te.type IN (2, 3)) = 2";

    /// <summary>SEC-94-09: the foreign key of the truncate guard table (it blocks TRUNCATE of the audit table) exists and is enabled.</summary>
    internal const string TruncateGuardPostConditionSql =
        "SELECT COUNT(*) FROM sys.foreign_keys fk WHERE fk.name = N'fk_audit_truncate_guard' " +
        "AND fk.parent_object_id = OBJECT_ID(N'dbo.AUDIT_LOG_TRUNCATE_GUARD') AND fk.referenced_object_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES') AND fk.is_disabled = 0";

    protected override async Task VerifyPostConditionsAsync(DbConnection connection, CancellationToken ct)
    {
        var trigger = Convert.ToInt64(await ScalarAsync(connection, AuditTriggerPostConditionSql, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (trigger != 1)
        {
            throw new GovernanceSchemaDriftException("The append-only audit trigger on AUDIT_LOG_ENTRIES is missing or disabled (fail closed).");
        }

        var guard = Convert.ToInt64(await ScalarAsync(connection, TruncateGuardPostConditionSql, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (guard != 1)
        {
            throw new GovernanceSchemaDriftException("The truncate guard of AUDIT_LOG_ENTRIES (fk_audit_truncate_guard) is missing or disabled (fail closed).");
        }
    }
}
