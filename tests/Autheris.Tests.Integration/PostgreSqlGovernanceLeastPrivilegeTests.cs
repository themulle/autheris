namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #94, plan D-12, section 7, SEC-94-12 on PostgreSQL: the schema is applied by the admin login; the runtime login holds only the
/// documented grants (no ownership, no DDL) and still starts in Mode=Verify, initializes the store and reads and writes the catalog.
/// </summary>
public sealed class PostgreSqlGovernanceLeastPrivilegeTests : IClassFixture<PostgreSqlGovernanceLeastPrivilegeTests.Fixture>
{
    private const string Password = "Lp-test-9f3a!";

    /// <summary>One migrated database and one runtime login for the whole class (the tests only read or fail against it).</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        public string Role { get; } = "autheris_rt_" + Guid.NewGuid().ToString("N")[..12];
        public PostgreSqlTestDatabase Db { get; private set; } = null!;
        public string RuntimeConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            Db = await PostgreSqlTestDatabase.CreateAsync();
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(Db.ConnectionString, GovernanceMigrationMode.Apply);
            await GrantRuntimeAsync(applyTableGrants: true);
        }

        public async Task DisposeAsync()
        {
            try
            {
                await ExecAdminAsync($"DROP OWNED BY \"{Role}\"; DROP ROLE IF EXISTS \"{Role}\";");
            }
            catch (PostgresException)
            {
                // best effort: the shared server is discarded with the run
            }

            await Db.DisposeAsync();
        }

        private async Task ExecAdminAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Creates the runtime login with the section 7 grants: no ownership, no DDL, no UPDATE/DELETE on the audit table.</summary>
        private async Task GrantRuntimeAsync(bool applyTableGrants)
        {
            var database = new NpgsqlConnectionStringBuilder(Db.ConnectionString).Database;
            await ExecAdminAsync($"CREATE ROLE \"{Role}\" LOGIN PASSWORD '{Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;");
            await ExecAdminAsync($"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO \"{Role}\"; GRANT USAGE ON SCHEMA public TO \"{Role}\";");
            if (applyTableGrants)
            {
                await ExecAdminAsync($$"""
                    DO $do$
                    DECLARE t text;
                    BEGIN
                      FOR t IN SELECT c.relname FROM pg_class c WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace LOOP
                        IF t = 'audit_log_entries' THEN
                          EXECUTE format('GRANT SELECT, INSERT ON %I TO "{{Role}}"', t);
                        ELSIF t = 'autheris_schema_migrations' THEN
                          EXECUTE format('GRANT SELECT ON %I TO "{{Role}}"', t);
                        ELSE
                          EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %I TO "{{Role}}"', t);
                        END IF;
                      END LOOP;
                      EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO "{{Role}}"', pg_get_serial_sequence('audit_log_entries', 'rowid'));
                    END
                    $do$;
                    """);
            }

            RuntimeConnectionString = new NpgsqlConnectionStringBuilder(Db.ConnectionString) { Username = Role, Password = Password, Pooling = false }.ConnectionString;
        }
    }

    private readonly Fixture _fx;

    public PostgreSqlGovernanceLeastPrivilegeTests(Fixture fixture) => _fx = fixture;

    private string _role => _fx.Role;

    private PostgreSqlTestDatabase _db => _fx.Db;

    private string _runtimeConnectionString => _fx.RuntimeConnectionString;

    private async Task<PostgresException> RuntimeFailsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_runtimeConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RuntimeLogin_StartsInVerify_InitializesStore_AndReadsAndWritesCatalogWithKeys()
    {
        var options = GovernanceSchemaTestSupport.PostgreSqlOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);

        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        var tableId = Guid.NewGuid();
        var identifier = new TableIdentifier("lpsource", "dbo", "readings");
        var idColumn = new TableColumn { TableId = tableId, ColumnName = "client_id", DataType = "varchar" };
        var tsColumn = new TableColumn { TableId = tableId, ColumnName = "ts", DataType = "datetime" };
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "readings", TableName = "readings", SchemaName = "dbo", SourceType = "PostgreSql", SourceName = "lpsource" },
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
    [Trait("Category", "Security")]
    public async Task ConsumerAggregation_RuntimeRoleWithoutCreate_RecordsAndReads()
    {
        // Issue #286 (AC-2, SEC-286-7): the CONSUMER_APP_* tables come from the baseline; the runtime role holds no CREATE right
        // (only SELECT, INSERT, UPDATE, DELETE) and still records usage, actors and query names, reads them and purges.
        var options = GovernanceSchemaTestSupport.PostgreSqlOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();
        var table = new TableIdentifier("lpsource", "dbo", "usage_" + Guid.NewGuid().ToString("N")[..8]);
        var tenant = new TenantId("tenant-lp");
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        var aggregator = new Autheris.Application.Usage.UsageAggregator();
        var recorder = new Autheris.Application.Usage.UsageRecorder(aggregator);
        var caller = new Autheris.Domain.Security.CallerContext("powerbi", "1.0", ClientId: "c1");
        var actor = new Sid("S-1-5-21-lp");
        using (var scope = Autheris.Application.Usage.UsageRequestScope.Begin(
            new Autheris.Application.Usage.UsageIdentity(tenant, actor, new System.Collections.Generic.HashSet<Sid>()),
            Autheris.Domain.Options.DataSources.DataSourceTransportFlags.Rest, caller, aggregator))
        {
            recorder.Record(new Autheris.Application.Usage.UsageFact(table, "lpsource", Autheris.Application.Usage.UsageOutcome.Allow, 20, true, 0, Array.Empty<Autheris.Application.Usage.GrantRef>(), default), tenant, actor);
            recorder.Record(new Autheris.Application.Usage.UsageFact(table, "lpsource", Autheris.Application.Usage.UsageOutcome.Allow, 30, true, 0, Array.Empty<Autheris.Application.Usage.GrantRef>(), default), tenant, actor);
            recorder.Record(new Autheris.Application.Usage.UsageFact(table, "lpsource", Autheris.Application.Usage.UsageOutcome.DenyAccess, 0, false, 0, Array.Empty<Autheris.Application.Usage.GrantRef>(), default), tenant, actor);
            recorder.Record(new Autheris.Application.Usage.UsageFact(table, "lpsource", Autheris.Application.Usage.UsageOutcome.Allow, 0, false, 0, Array.Empty<Autheris.Application.Usage.GrantRef>(), default), tenant, actor);
            recorder.Record(new Autheris.Application.Usage.UsageFact(table, "lpsource", Autheris.Application.Usage.UsageOutcome.Allow, 0, false, 0, Array.Empty<Autheris.Application.Usage.GrantRef>(), default), tenant, actor);
            scope.Complete(10, 100, 200);
        }

        await repository.AccumulateAsync(aggregator.Swap());
        await repository.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 4);
        await repository.RecordQueryNameAsync(table, tenant, day, "powerbi", "GetReadings", 1);

        var rows = await repository.QueryConsumerAppDailyAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var actors = await repository.QueryDistinctActorCountsAsync(table, tenant, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await repository.PurgeExpiredConsumerMetricsAsync(400, DateTimeOffset.UtcNow);

        var row = rows.ShouldHaveSingleItem();
        row.Calls.ShouldBe(5);
        row.RowsReturned.ShouldBe(50);
        actors["powerbi"].ShouldBe(1);
        row.QueryNames.ShouldBe(["GetReadings"]);
        (await RuntimeFailsAsync("CREATE TABLE consumer_app_runtime_ddl (a int)")).SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task RuntimeLogin_CannotUpdateAudit_DisableTrigger_OrWriteMigrationTable()
    {
        foreach (var sql in new[]
                 {
                     "UPDATE audit_log_entries SET decision = 'x'",
                     "DELETE FROM audit_log_entries",
                     "TRUNCATE audit_log_entries",
                     "ALTER TABLE audit_log_entries DISABLE TRIGGER trg_audit_append_only_row",
                     "DROP TRIGGER trg_audit_append_only_row ON audit_log_entries",
                     "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (99, 'x', 'x', 'x', 'x')",
                     "UPDATE autheris_schema_migrations SET checksum = 'x'",
                     "DELETE FROM autheris_schema_migrations",
                     "CREATE TABLE public.rogue (id INT)",
                     "DROP TABLE tables"
                 })
        {
            var failure = await RuntimeFailsAsync(sql);
            failure.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }

    [Fact]
    public async Task RuntimeLogin_IsNeitherOwnerNorMemberOfTheOwnerRole()
    {
        await using var conn = new NpgsqlConnection(_runtimeConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_has_role(current_user, c.relowner, 'USAGE') OR pg_get_userbyid(c.relowner) = current_user FROM pg_class c WHERE c.oid = 'audit_log_entries'::regclass";
        ((bool)(await cmd.ExecuteScalarAsync())!).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_WithRuntimeLogin_OnUnmigratedDatabase_FailsWithClearMessage()
    {
        await using var fresh = await PostgreSqlTestDatabase.CreateAsync();
        var database = new NpgsqlConnectionStringBuilder(fresh.ConnectionString).Database;
        var role = _role + "b";
        await using (var admin = new NpgsqlConnection(fresh.ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{Password}'; REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var runtime = new NpgsqlConnectionStringBuilder(fresh.ConnectionString) { Username = role, Password = Password, Pooling = false }.ConnectionString;

            var apply = await Should.ThrowAsync<PostgresException>(() => GovernanceSchemaTestSupport.RunPostgreSqlAsync(runtime, GovernanceMigrationMode.Apply));
            apply.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            apply.MessageText.ShouldContain("permission denied");

            var verify = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => GovernanceSchemaTestSupport.RunPostgreSqlAsync(runtime, GovernanceMigrationMode.Verify));
            verify.Message.ShouldContain("pending migrations 0001");
        }
        finally
        {
            await using var admin = new NpgsqlConnection(fresh.ConnectionString);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP OWNED BY \"{role}\"; DROP ROLE IF EXISTS \"{role}\";";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RuntimeLogin_CanSetReadAndClearDatasetTagsAndVisibility_OnTheNewTables()
    {
        // Issue #111: the runtime login needs SELECT/INSERT/UPDATE/DELETE on TABLE_TAGS and TABLE_CATALOG_SETTINGS; the generic runtime grant
        // covers both, and the tag/tier flow (including the tier overlay on every catalog read) works without any DDL right.
        var options = GovernanceSchemaTestSupport.PostgreSqlOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        var identifier = new TableIdentifier("lpsource", "dbo", "tags_" + Guid.NewGuid().ToString("N")[..8]);
        await repository.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "t", TableName = identifier.TableName, SchemaName = "dbo", SourceType = "PostgreSql", SourceName = "lpsource" },
            Identifier = identifier,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        });
        var actor = new Sid("S-1-5-21-lp");

        (await repository.ReplaceDatasetTagsAsync(identifier, ["finance", "pii"], DatasetTagSource.Admin, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, ["dbt-tag"], DatasetTagSource.Dbt, actor)).ShouldNotBeNull();
        (await repository.ChangeDatasetTagsAsync(identifier, ["extra"], ["pii"], actor)).ShouldNotBeNull();
        (await repository.SetDatasetDiscoveryAsync(identifier, true, null, null, actor)).ShouldNotBeNull();

        var read = (await repository.GetTableMetadataAsync(identifier))!;
        read.Table.DiscoveryHidden.ShouldBeTrue();
        read.CatalogTags.ShouldBe(["dbt-tag", "extra", "finance"], ignoreOrder: true);
        (await repository.GetAllTablesAsync()).Single(t => t.Identifier == identifier).Table.DiscoveryHidden.ShouldBeTrue();

        (await repository.SetDatasetDiscoveryAsync(identifier, false, null, null, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, [], DatasetTagSource.Admin, actor)).ShouldNotBeNull();
        (await repository.ReplaceDatasetTagsAsync(identifier, [], DatasetTagSource.Dbt, actor)).ShouldNotBeNull();
        (await repository.GetTableMetadataAsync(identifier))!.CatalogTags.ShouldBeEmpty();
    }

}
