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
        var options = GovernanceSchemaTestSupport.PostgreSqlOptions(_runtimeConnectionString, GovernanceMigrationMode.Verify);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
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
}
