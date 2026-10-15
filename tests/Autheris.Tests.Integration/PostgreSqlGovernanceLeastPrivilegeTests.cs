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
public sealed class PostgreSqlGovernanceLeastPrivilegeTests : IAsyncLifetime
{
    private const string Password = "Lp-test-9f3a!";

    private readonly string _role = "autheris_rt_" + Guid.NewGuid().ToString("N")[..12];
    private PostgreSqlTestDatabase _db = null!;
    private string _runtimeConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Apply);
        await GrantRuntimeAsync(applyTableGrants: true);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await ExecAdminAsync($"DROP OWNED BY \"{_role}\"; DROP ROLE IF EXISTS \"{_role}\";");
        }
        catch (PostgresException)
        {
            // best effort: the shared server is discarded with the run
        }

        await _db.DisposeAsync();
    }

    private async Task ExecAdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Creates the runtime login with the section 7 grants: no ownership, no DDL, no UPDATE/DELETE on the audit table.</summary>
    private async Task GrantRuntimeAsync(bool applyTableGrants)
    {
        var database = new NpgsqlConnectionStringBuilder(_db.ConnectionString).Database;
        await ExecAdminAsync($"CREATE ROLE \"{_role}\" LOGIN PASSWORD '{Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;");
        await ExecAdminAsync($"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO \"{_role}\"; GRANT USAGE ON SCHEMA public TO \"{_role}\";");
        if (applyTableGrants)
        {
            await ExecAdminAsync($$"""
                DO $do$
                DECLARE t text;
                BEGIN
                  FOR t IN SELECT c.relname FROM pg_class c WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace LOOP
                    IF t = 'audit_log_entries' THEN
                      EXECUTE format('GRANT SELECT, INSERT ON %I TO "{{_role}}"', t);
                    ELSIF t = 'autheris_schema_migrations' THEN
                      EXECUTE format('GRANT SELECT ON %I TO "{{_role}}"', t);
                    ELSE
                      EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %I TO "{{_role}}"', t);
                    END IF;
                  END LOOP;
                  EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO "{{_role}}"', pg_get_serial_sequence('audit_log_entries', 'rowid'));
                END
                $do$;
                """);
        }

        _runtimeConnectionString = new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Username = _role, Password = Password, Pooling = false }.ConnectionString;
    }

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
