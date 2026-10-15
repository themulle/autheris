namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #94, plan D-12, section 7, SEC-94-12 on SQL Server: the schema is applied by the admin login; the runtime login holds only the
/// documented grants (VIEW DEFINITION only on AUDIT_LOG_TRUNCATE_GUARD so that Verify can see it, no data grant, no ALTER/CONTROL/db_owner/db_ddladmin) and still starts in Mode=Verify,
/// initializes the store and reads and writes the catalog.
/// </summary>
public sealed class SqlServerGovernanceLeastPrivilegeTests : IAsyncLifetime
{
    private const string Password = "Lp-test-9f3a!x";

    private readonly string _login = "autheris_rt_" + Guid.NewGuid().ToString("N")[..12];
    private SqlServerTestDatabase _db = null!;
    private string _runtimeConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_db.ConnectionString, GovernanceMigrationMode.Apply);
        await CreateRuntimeLoginAsync(_db.ConnectionString, _login, grantTables: true);
        _runtimeConnectionString = new SqlConnectionStringBuilder(_db.ConnectionString) { UserID = _login, Password = Password, Pooling = false }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await DropLoginAsync(_db.ConnectionString, _login);
        await _db.DisposeAsync();
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Creates the runtime login with the section 7 grants.</summary>
    private static async Task CreateRuntimeLoginAsync(string adminConnectionString, string login, bool grantTables)
    {
        var master = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF;");
        await ExecAsync(adminConnectionString, $"CREATE USER [{login}] FOR LOGIN [{login}];");
        if (grantTables)
        {
            await ExecAsync(adminConnectionString, $$"""
                DECLARE @t sysname, @sql nvarchar(max);
                DECLARE c CURSOR FOR SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo');
                OPEN c; FETCH NEXT FROM c INTO @t;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                  SET @sql = CASE
                    WHEN @t = N'AUDIT_LOG_ENTRIES' THEN N'GRANT SELECT, INSERT ON dbo.' + QUOTENAME(@t) + N' TO [{{login}}]'
                    -- Metadata visibility only: without it the guard table is invisible and Verify reports it as missing (fail closed).
                    WHEN @t = N'AUDIT_LOG_TRUNCATE_GUARD' THEN N'GRANT VIEW DEFINITION ON dbo.' + QUOTENAME(@t) + N' TO [{{login}}]'
                    WHEN @t = N'autheris_schema_migrations' THEN N'GRANT SELECT ON dbo.' + QUOTENAME(@t) + N' TO [{{login}}]'
                    ELSE N'GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.' + QUOTENAME(@t) + N' TO [{{login}}]' END;
                  IF @sql IS NOT NULL EXEC sp_executesql @sql;
                  FETCH NEXT FROM c INTO @t;
                END
                CLOSE c; DEALLOCATE c;
                """);
        }
    }

    private static async Task DropLoginAsync(string adminConnectionString, string login)
    {
        try
        {
            var master = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = "master" }.ConnectionString;
            await ExecAsync(adminConnectionString, $"IF USER_ID(N'{login}') IS NOT NULL DROP USER [{login}];");
            await ExecAsync(master, $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
        }
        catch (SqlException)
        {
            // best effort: the shared server is discarded with the run
        }
    }

    private async Task<SqlException> RuntimeFailsAsync(string sql)
    {
        await using var conn = new SqlConnection(_runtimeConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await Should.ThrowAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RuntimeLogin_StartsInVerify_InitializesStore_AndReadsAndWritesCatalogWithKeys()
    {
        var options = GovernanceSchemaTestSupport.SqlServerOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);

        await new SqlServerGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("lpsource", "dbo", "readings");
        var idColumn = new TableColumn { TableId = tableId, ColumnName = "client_id", DataType = "varchar" };
        var tsColumn = new TableColumn { TableId = tableId, ColumnName = "ts", DataType = "datetime" };
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "readings", TableName = "readings", SchemaName = "dbo", SourceType = "Sqlite", SourceName = "lpsource" },
            Identifier = identifier,
            Columns = [idColumn, tsColumn],
            PrimaryKeyColumns = ["client_id", "ts"],
            Indexes = [new TableIndexDefinition("ux_readings_ts", ["ts"], true)],
            ReplaceKeyDefinition = true
        });

        var read = await repository.GetTableMetadataAsync(identifier);

        read.ShouldNotBeNull();
        read.Columns.Select(c => c.ColumnName).ShouldBe(["client_id", "ts"], ignoreOrder: true);
        read.PrimaryKeyColumns.ShouldBe(["client_id", "ts"]);
        read.Indexes.ShouldHaveSingleItem().Columns.ShouldBe(["ts"]);
    }

    [Fact]
    public async Task RuntimeLogin_CannotUpdateAudit_DisableTrigger_OrWriteMigrationTable()
    {
        foreach (var sql in new[]
                 {
                     "UPDATE dbo.AUDIT_LOG_ENTRIES SET decision = N'x'",
                     "DELETE FROM dbo.AUDIT_LOG_ENTRIES",
                     "TRUNCATE TABLE dbo.AUDIT_LOG_ENTRIES",
                     "TRUNCATE TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD",
                     "INSERT INTO dbo.AUDIT_LOG_TRUNCATE_GUARD (id, seq) VALUES (1, NULL)",
                     "DISABLE TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES",
                     "DROP TRIGGER dbo.trg_audit_append_only",
                     "ALTER TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD NOCHECK CONSTRAINT fk_audit_truncate_guard",
                     "INSERT INTO dbo.autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (99, N'x', N'x', N'x', N'x')",
                     "UPDATE dbo.autheris_schema_migrations SET checksum = N'x'",
                     "DELETE FROM dbo.autheris_schema_migrations",
                     "CREATE TABLE dbo.rogue (id INT)",
                     "DROP TABLE dbo.TABLES"
                 })
        {
            await RuntimeFailsAsync(sql);
        }

        // The audit protection is intact after all attempts.
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_db.ConnectionString, GovernanceMigrationMode.Verify);
    }

    [Fact]
    public async Task RuntimeLogin_HasNoDdlRoleAndNoDataGrantOnTheTruncateGuard()
    {
        await using var conn = new SqlConnection(_runtimeConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT IS_MEMBER(N'db_owner') + IS_MEMBER(N'db_ddladmin')
                 + CAST(HAS_PERMS_BY_NAME(N'dbo.AUDIT_LOG_TRUNCATE_GUARD', N'OBJECT', N'SELECT') AS int)
                 + CAST(HAS_PERMS_BY_NAME(N'dbo.AUDIT_LOG_ENTRIES', N'OBJECT', N'ALTER') AS int)
                 + CAST(HAS_PERMS_BY_NAME(N'dbo.AUDIT_LOG_ENTRIES', N'OBJECT', N'UPDATE') AS int)
                 + CAST(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE') AS int)
            """;
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Apply_WithRuntimeLogin_OnUnmigratedDatabase_FailsWithClearMessage()
    {
        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        var login = _login + "b";
        await CreateRuntimeLoginAsync(fresh.ConnectionString, login, grantTables: false);
        try
        {
            var runtime = new SqlConnectionStringBuilder(fresh.ConnectionString) { UserID = login, Password = Password, Pooling = false }.ConnectionString;

            var apply = await Should.ThrowAsync<SqlException>(() => GovernanceSchemaTestSupport.RunSqlServerAsync(runtime, GovernanceMigrationMode.Apply));
            apply.Message.ShouldContain("permission denied");

            var verify = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => GovernanceSchemaTestSupport.RunSqlServerAsync(runtime, GovernanceMigrationMode.Verify));
            verify.Message.ShouldContain("pending migrations 0001");
        }
        finally
        {
            await DropLoginAsync(fresh.ConnectionString, login);
        }
    }
}
