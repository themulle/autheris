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
            }),
        .. SqlMigrationStep.LoadEmbedded("sqlserver").Select(step => step.ToMigrationStep())
    ];

    private static readonly GovernanceSchemaShapeVerifier Verifier = new("sqlserver", new SqlServerSchemaCatalogReader());

    private readonly SqlServerGovernanceRepository _repository;

    public SqlServerGovernanceMigrationRunner(
        SqlServerGovernanceRepository repository,
        IOptions<GatewayOptions> options,
        ILogger<SqlServerGovernanceMigrationRunner>? logger = null,
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
        // HAS_PERMS_BY_NAME: read-only introspection of the effective permissions (role membership included).
        var writeHistory = await ScalarAsync(
            connection,
            "SELECT CASE WHEN OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NOT NULL AND (HAS_PERMS_BY_NAME(N'dbo.autheris_schema_migrations', N'OBJECT', N'INSERT') = 1 OR HAS_PERMS_BY_NAME(N'dbo.autheris_schema_migrations', N'OBJECT', N'UPDATE') = 1 OR HAS_PERMS_BY_NAME(N'dbo.autheris_schema_migrations', N'OBJECT', N'DELETE') = 1) THEN 1 ELSE 0 END",
            ct).ConfigureAwait(false);
        if (Convert.ToInt32(writeHistory, CultureInfo.InvariantCulture) == 1)
        {
            return "write access to dbo.autheris_schema_migrations";
        }

        var alterSchema = await ScalarAsync(
            connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER') = 1 THEN 1 ELSE 0 END", ct).ConfigureAwait(false);
        return Convert.ToInt32(alterSchema, CultureInfo.InvariantCulture) == 1 ? "ALTER privilege on schema dbo" : null;
    }

    protected override Task RecordRuntimeDdlAuditAsync(int appliedSteps, CancellationToken ct) =>
        _repository.RecordAuditEventAsync(RuntimeDdlAudit.Build(appliedSteps), ct);

    public override IReadOnlyList<MigrationStep> Steps => Catalogue;

    internal override GovernanceSchemaShapeVerifier ShapeVerifier => Verifier;

    protected override string EngineDisplayName => "SQL Server";

    protected override string CreateVersionTableSql =>
        "IF OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NULL CREATE TABLE dbo.autheris_schema_migrations (version INT NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL, checksum NVARCHAR(128) NOT NULL, applied_at NVARCHAR(64) NOT NULL, applied_by NVARCHAR(256) NOT NULL)";

    protected override string SelectVersionsSql => "SELECT version, checksum, name FROM dbo.autheris_schema_migrations ORDER BY version";

    protected override string HistoryTableExistsSql => "SELECT CASE WHEN OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NULL THEN 0 ELSE 1 END";

    protected override string InsertVersionSql =>
        "INSERT INTO dbo.autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by)";

    protected override async Task<DbConnection> OpenConnectionAsync(bool verifyOnly, CancellationToken ct)
    {
        // Mode=Verify only reads: it runs with the runtime login, never with the DDL login (least privilege, plan D-12).
        var connectionString = verifyOnly || string.IsNullOrWhiteSpace(_repository.MigrationConnectionString)
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

    protected override async Task AcquireLockAsync(DbConnection connection, TimeSpan timeout, CancellationToken ct)
    {
        await using var lockCmd = connection.CreateCommand();
        lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @r;";
        var p = lockCmd.CreateParameter();
        p.ParameterName = "@res";
        p.Value = SchemaAppLockResource;
        lockCmd.Parameters.Add(p);
        var t = lockCmd.CreateParameter();
        t.ParameterName = "@timeout";
        t.Value = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        lockCmd.Parameters.Add(t);
        var rc = Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (rc < 0)
        {
            throw ApplockFailure(rc, Options.LockTimeoutSeconds);
        }
    }

    /// <summary>I388-SC-09: every negative <c>sp_getapplock</c> return code fails closed; -1 is the timeout, the others are a generic lock failure.</summary>
    internal static GovernanceSchemaDriftException ApplockFailure(int returnCode, int timeoutSeconds) =>
        returnCode == -1
            ? LockTimeoutError("SQL Server", timeoutSeconds)
            : new GovernanceSchemaDriftException(
                GovernanceMigrationCodes.LockFailed,
                $"The SQL Server schema application lock could not be acquired (sp_getapplock returned {returnCode}).");

    /// <summary>I388-SC-08: <c>XACT_ABORT ON</c> and a lock timeout for the step transaction.</summary>
    protected override Task BeginStepBoundsAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "SET XACT_ABORT ON; SET LOCK_TIMEOUT " + (Options.StepLockTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture),
            ct);

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

    protected override async Task VerifyPostConditionsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        var trigger = Convert.ToInt64(await ScalarAsync(connection, AuditTriggerPostConditionSql, ct, transaction).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (trigger != 1)
        {
            throw new GovernanceSchemaDriftException("The append-only audit trigger on AUDIT_LOG_ENTRIES is missing or disabled (fail closed).");
        }

        var guard = Convert.ToInt64(await ScalarAsync(connection, TruncateGuardPostConditionSql, ct, transaction).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (guard != 1)
        {
            throw new GovernanceSchemaDriftException("The truncate guard of AUDIT_LOG_ENTRIES (fk_audit_truncate_guard) is missing or disabled (fail closed).");
        }
    }
}
