using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>Migration catalogue and runner for the SQL Server governance store. Honors <c>GovernanceDb:MigrationConnectionString</c> (separate DDL login).</summary>
public sealed class SqlServerGovernanceMigrationRunner : GovernanceMigrationRunnerBase
{
    /// <summary>Application lock resource that serialises the schema DDL of concurrently starting replicas.</summary>
    private const string SchemaAppLockResource = "autheris:governance:schema";

    /// <summary>Durable, fenced CDC watermarks (WP-ARC-A-11).</summary>
    private const string CdcWatermarksDdl = @"IF OBJECT_ID(N'dbo.autheris_cdc_watermarks', N'U') IS NULL CREATE TABLE dbo.autheris_cdc_watermarks (source NVARCHAR(32) NOT NULL, domain_name NVARCHAR(128) NOT NULL, schema_name NVARCHAR(128) NOT NULL, table_name NVARCHAR(128) NOT NULL, watermark BIGINT NOT NULL, fencing_token BIGINT NOT NULL, updated_at NVARCHAR(64) NOT NULL, updated_by NVARCHAR(256) NOT NULL, CONSTRAINT PK_autheris_cdc_watermarks PRIMARY KEY (source, domain_name, schema_name, table_name))";

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
        new MigrationStep(
            2,
            "webhook_outbox",
            MigrationStep.ComputeChecksum("sqlserver:0002:webhook_outbox:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, WebhookOutboxSchema.SqlServer, ctx.CancellationToken),
            Transactional: true),
        new MigrationStep(
            3,
            "cdc_watermarks",
            MigrationStep.ComputeChecksum("sqlserver:0003:cdc_watermarks:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, CdcWatermarksDdl, ctx.CancellationToken), Transactional: true),
        new MigrationStep(
            4,
            "cloudevent_subscriptions",
            MigrationStep.ComputeChecksum("sqlserver:0004:cloudevent_subscriptions:v1"),
            ctx => ExecuteAsync(ctx.Connection, ctx.Transaction, CloudEventSubscriptionsSchema.SqlServer, ctx.CancellationToken),
            Transactional: true)
    ];

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

    protected override string CreateVersionTableSql =>
        "IF OBJECT_ID(N'dbo.autheris_schema_migrations', N'U') IS NULL CREATE TABLE dbo.autheris_schema_migrations (version INT NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL, checksum NVARCHAR(128) NOT NULL, applied_at NVARCHAR(64) NOT NULL, applied_by NVARCHAR(256) NOT NULL)";

    protected override string SelectVersionsSql => "SELECT version, checksum FROM dbo.autheris_schema_migrations ORDER BY version";

    protected override string InsertVersionSql =>
        "INSERT INTO dbo.autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (@version, @name, @checksum, @applied_at, @applied_by)";

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connectionString = string.IsNullOrWhiteSpace(_repository.MigrationConnectionString)
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

    protected override async Task VerifyPostConditionsAsync(DbConnection connection, CancellationToken ct)
    {
        var count = Convert.ToInt64(
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sys.triggers WHERE name = N'trg_audit_append_only' AND parent_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES')",
                ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new GovernanceSchemaDriftException("The append-only audit trigger on AUDIT_LOG_ENTRIES is missing after the governance migration.");
        }
    }
}
