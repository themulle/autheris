namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>Issue #388 (WP-388-3) on SQL Server: see <see cref="GovernanceUpgradeTestsBase"/>.</summary>
[Trait("Category", "Security")]
public sealed class SqlServerGovernanceUpgradeTests : GovernanceUpgradeTestsBase
{
    private const string Password = "Lp-test-9f3a!x";
    private const string SchemaAppLockResource = "autheris:governance:schema";

    protected override string Engine => "sqlserver";
    protected override string ConsentsTable => "CONSENTS";
    protected override string ProbeColumnType => "NVARCHAR";
    protected override string AddProbeColumnSql => "IF COL_LENGTH(N'dbo.CONSENTS', N'probe_388') IS NULL ALTER TABLE dbo.CONSENTS ADD probe_388 NVARCHAR(50) NULL";
    protected override string ProbeColumnCountSql => "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CONSENTS') AND name = N'probe_388'";
    protected override string TriggerCountSql => "SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES') AND is_disabled = 0";
    protected override string AuditHashesSql => "SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max), (SELECT STRING_AGG(CONVERT(nvarchar(max), entry_hash), ',') WITHIN GROUP (ORDER BY seq) FROM dbo.AUDIT_LOG_ENTRIES))), 2)";
    protected override string HistoryTableCountSql => "SELECT COUNT(*) FROM sys.tables WHERE name = N'autheris_schema_migrations'";
    protected override string DropSomeBaselineTableSql => "DROP TABLE dbo.CONSUMER_APP_DAILY";
    protected override string ForeignOldShapeTableSql => "CREATE TABLE dbo.CONSENT_COLUMN_RULES (id NVARCHAR(128) NOT NULL PRIMARY KEY)";

    protected override string FingerprintSql =>
        "SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max), ISNULL((SELECT STRING_AGG(CONVERT(nvarchar(max), t.name + N'.' + c.name + N':' + ty.name + N':' + CONVERT(nvarchar(1), c.is_nullable)), N',') WITHIN GROUP (ORDER BY t.name, c.column_id)"
        + " FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id), N''))), 2)"
        + " + N':' + CONVERT(varchar(20), (SELECT COUNT(*) FROM sys.indexes WHERE object_id IN (SELECT object_id FROM sys.tables)))"
        + " + N':' + CONVERT(varchar(20), (SELECT COUNT(*) FROM sys.triggers))"
        + " + N':' + CONVERT(varchar(20), (SELECT COUNT(*) FROM sys.tables))";

    private static IOptions<GatewayOptions> Opts(string cs, GovernanceMigrationMode mode, bool allowNewer, int lockTimeoutSeconds, bool requireLeastPrivilege) =>
        Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "SqlServer",
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
        var db = await SqlServerTestDatabase.CreateAsync();
        return db.IsAvailable ? new TestDb(db.ConnectionString, db.DisposeAsync) : null;
    }

    protected override async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<string> QueryAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
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
        List<string>? trace = null)
    {
        var options = Opts(connectionString, mode, allowNewer, lockTimeoutSeconds, requireLeastPrivilege);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        var runner = new SqlServerGovernanceMigrationRunner(
            repository, options, log is null ? null : new CaptureLogger<SqlServerGovernanceMigrationRunner>(log))
        {
            AdditionalSteps = extra ?? [],
            Trace = trace is null ? null : s => { lock (trace) { trace.Add(s); } }
        };
        await runner.RunAsync();
    }

    protected override async Task SeedV150Async(string connectionString)
    {
        await using var repository = GovernanceStoreFactory.CreateSqlServer(
            Substitute.For<IEpochValidationService>(), Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false));
        await SeedCoreAsync(repository, () => repository.FlushAuditChannelAsync());
    }

    protected override async Task<bool> AuditChainVerifiesAsync(string connectionString, IReadOnlyList<MigrationStep> extra)
    {
        var options = Opts(connectionString, GovernanceMigrationMode.Apply, false, 300, false);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repository, options) { AdditionalSteps = extra }.RunAsync();
        await repository.InitializeAsync();
        return await repository.VerifyAuditHashChainAsync();
    }

    protected override async Task<IAsyncDisposable> HoldSchemaLockAsync(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'{SchemaAppLockResource}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
        var rc = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        if (rc < 0)
        {
            await conn.DisposeAsync();
            throw new InvalidOperationException("The test could not take the schema application lock (sp_getapplock returned " + rc + ").");
        }

        return conn; // disposing the session releases the application lock
    }

    protected override async Task<(string ConnectionString, Func<Task> Cleanup)> CreateReadOnlyLoginAsync(TestDb db, bool writeOnHistory)
    {
        var login = "autheris_up_" + Guid.NewGuid().ToString("N")[..12];
        var master = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF;");
        await ExecAsync(db.ConnectionString, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON SCHEMA::dbo TO [{login}]; GRANT VIEW DEFINITION ON SCHEMA::dbo TO [{login}];");
        if (writeOnHistory)
        {
            await ExecAsync(db.ConnectionString, $"GRANT INSERT ON dbo.autheris_schema_migrations TO [{login}];");
        }

        var cs = new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = Password, Pooling = false }.ConnectionString;
        return (cs, async () =>
        {
            try
            {
                await ExecAsync(db.ConnectionString, $"IF USER_ID(N'{login}') IS NOT NULL DROP USER [{login}];");
                await ExecAsync(master, $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
            }
            catch (SqlException)
            {
                // best effort: the shared server is discarded with the run
            }
        });
    }
}
