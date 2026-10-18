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
public sealed class SqlServerGovernanceLeastPrivilegeTests : IClassFixture<SqlServerGovernanceLeastPrivilegeTests.Fixture>
{
    private const string Password = "Lp-test-9f3a!x";

    /// <summary>One migrated database and one runtime login for the whole class (the tests only read or fail against it).</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        public string Login { get; } = "autheris_rt_" + Guid.NewGuid().ToString("N")[..12];
        public SqlServerTestDatabase Db { get; private set; } = null!;
        public string RuntimeConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            Db = await SqlServerTestDatabase.CreateAsync();
            await GovernanceSchemaTestSupport.RunSqlServerAsync(Db.ConnectionString, GovernanceMigrationMode.Apply);
            await CreateRuntimeLoginAsync(Db.ConnectionString, Login, grantTables: true);
            RuntimeConnectionString = new SqlConnectionStringBuilder(Db.ConnectionString) { UserID = Login, Password = Password, Pooling = false }.ConnectionString;
        }

        public async Task DisposeAsync()
        {
            await DropLoginAsync(Db.ConnectionString, Login);
            await Db.DisposeAsync();
        }
    }

    private readonly Fixture _fx;

    public SqlServerGovernanceLeastPrivilegeTests(Fixture fixture) => _fx = fixture;

    private string _login => _fx.Login;

    private SqlServerTestDatabase _db => _fx.Db;

    private string _runtimeConnectionString => _fx.RuntimeConnectionString;

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
    public async Task RuntimeLogin_CanSetReadAndClearRowFilterEvaluation_OnTheNewTableAndColumns()
    {
        // Issue #107 (plan section 7): the runtime login needs SELECT/INSERT/UPDATE/DELETE on TABLE_ROW_FILTER_STRATEGIES and UPDATE on
        // CONSENT_ROW_FILTERS; the generic runtime grant covers both, and the settings flow works without any DDL right.
        var options = GovernanceSchemaTestSupport.SqlServerOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("lpsource", "dbo", "rfeval_" + Guid.NewGuid().ToString("N")[..8]);
        var column = new TableColumn { TableId = tableId, ColumnName = "customer_id", DataType = "int" };
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "t", TableName = identifier.TableName, SchemaName = "dbo", SourceType = "Sqlite", SourceName = "lpsource" },
            Identifier = identifier,
            Columns = [column]
        });
        var columnId = (await repository.GetTableMetadataAsync(identifier))!.Columns[0].Id;
        var tenant = new TenantId("tenant-lp");
        var consent = await repository.CreateConsentAsync(new Consent
        {
            TableId = tableId, TableIdentifier = identifier, TenantId = tenant, GranteeType = GranteeType.User, GranteeSid = new Sid("S-1-5-21-lp"),
            Effect = ConsentEffect.Allow, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            RowFilters = [new ConsentRowFilter { TableColumnId = columnId, ColumnName = "customer_id", FilterType = RowFilterType.SubqueryCorrelated,
                DependentTable = new TableIdentifier("lpsource", "dbo", "customers"), ForeignKeyColumn = "customer_id", PrimaryKeyColumn = "id", SubqueryFilterPredicateJson = "[]" }]
        });
        var ruleId = (await repository.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-lp")], identifier, DateTimeOffset.UtcNow, tenant))
            .Single(c => c.Id == consent.Id).RowFilters.Single().Id;
        var settings = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 10);

        (await repository.UpdateRowFilterEvaluationAsync(ruleId, tenant, settings)).ShouldNotBeNull();
        (await repository.SetRowFilterEvaluationAsync(identifier, settings, new Sid("S-1-5-21-lp"))).ShouldNotBeNull();
        (await repository.GetTableMetadataAsync(identifier))!.RowFilterEvaluation.ShouldBe(settings);
        (await repository.SetRowFilterEvaluationAsync(identifier, null, new Sid("S-1-5-21-lp"))).ShouldNotBeNull();
        (await repository.GetTableMetadataAsync(identifier))!.RowFilterEvaluation.ShouldBeNull();
    }

    [Fact]
    public async Task RuntimeLogin_CanSetReadAndClearDatasetTagsAndVisibility_OnTheNewTables()
    {
        // Issue #111: the runtime login needs SELECT/INSERT/UPDATE/DELETE on TABLE_TAGS and TABLE_CATALOG_SETTINGS; the generic runtime grant
        // covers both, and the tag/tier flow (including the tier overlay on every catalog read) works without any DDL right.
        var options = GovernanceSchemaTestSupport.SqlServerOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedSqlServer(options);
        await new SqlServerGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        var identifier = new TableIdentifier("lpsource", "dbo", "tags_" + Guid.NewGuid().ToString("N")[..8]);
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "t", TableName = identifier.TableName, SchemaName = "dbo", SourceType = "Sqlite", SourceName = "lpsource" },
            Identifier = identifier,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        });
        var actor = new Sid("S-1-5-21-lp");

        (await repository.ReplaceDatasetTagsAsync(identifier, ["finance", "pii"], DatasetTagSource.Admin, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, ["dbt-tag"], DatasetTagSource.Dbt, actor)).ShouldNotBeNull();
        (await repository.ChangeDatasetTagsAsync(identifier, ["extra"], ["pii"], actor)).ShouldNotBeNull();
        (await repository.SetDatasetVisibilityAsync(identifier, DatasetVisibility.Full, actor)).ShouldNotBeNull();

        var read = (await repository.GetTableMetadataAsync(identifier))!;
        read.Table.Visibility.ShouldBe(DatasetVisibility.Full);
        read.CatalogTags.ShouldBe(["dbt-tag", "extra", "finance"], ignoreOrder: true);
        (await repository.GetAllTablesAsync()).Single(t => t.Identifier == identifier).Table.Visibility.ShouldBe(DatasetVisibility.Full);

        (await repository.SetDatasetVisibilityAsync(identifier, DatasetVisibility.Hidden, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, [], DatasetTagSource.Admin, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, [], DatasetTagSource.Dbt, actor)).ShouldNotBeNull();
        (await repository.GetTableMetadataAsync(identifier))!.CatalogTags.ShouldBeEmpty();
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
