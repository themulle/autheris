namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>Step 0043 (<c>single_approval_cleanup</c>): pending items are discarded (never active), draft columns are dropped, approved rows survive.</summary>
[Trait("Category", "Security")]
public sealed class SingleApprovalCleanupMigrationStep0043Tests
{
    private const string StepName = "single_approval_cleanup";

    private const string PostgreSqlPreShape = """
        CREATE SCHEMA pre_step_0043;
        SET search_path TO pre_step_0043;
        CREATE TABLE CALLER_ATTRIBUTES (
            id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, definition_json TEXT NOT NULL, definition_hash TEXT NOT NULL,
            status INTEGER NOT NULL, created_by TEXT NOT NULL, approved_by TEXT, approved_at TEXT,
            draft_definition_json TEXT, draft_definition_hash TEXT, draft_created_by TEXT,
            pending_deletion INTEGER NOT NULL DEFAULT 0, deletion_requested_by TEXT, updated_by TEXT, updated_at TEXT NOT NULL);
        CREATE TABLE CALLER_ATTRIBUTE_SERVICE_SETS (
            id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, application_id TEXT NOT NULL, attribute_name TEXT NOT NULL, values_json TEXT NOT NULL,
            values_hash TEXT NOT NULL, status INTEGER NOT NULL, created_by TEXT NOT NULL, approved_by TEXT, approved_at TEXT,
            draft_values_json TEXT, draft_created_by TEXT, updated_at TEXT NOT NULL);
        CREATE TABLE FUNCTIONAL_RIGHT_MAPPINGS (
            tenant_id VARCHAR(64) NOT NULL, realm_id VARCHAR(64) NOT NULL, group_or_role VARCHAR(128) NOT NULL, functional_right VARCHAR(64) NOT NULL,
            approved_by VARCHAR(128), approved_at TIMESTAMP WITH TIME ZONE, created_by VARCHAR(128) NOT NULL);
        CREATE TABLE VIRTUAL_FILTERS (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, status VARCHAR(32) NOT NULL DEFAULT 'Active');
        CREATE TABLE VIRTUAL_FILTER_ACCESS_PROFILES (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, definition_json TEXT NOT NULL);
        CREATE TABLE CALLER_ATTRIBUTE_GENERATION (id INTEGER PRIMARY KEY, generation BIGINT NOT NULL);
        CREATE TABLE VIRTUAL_FILTER_GENERATION (id INTEGER PRIMARY KEY, generation BIGINT NOT NULL);
        INSERT INTO CALLER_ATTRIBUTE_GENERATION VALUES (1, 5);
        INSERT INTO VIRTUAL_FILTER_GENERATION VALUES (1, 7);
        """;

    private const string SqlServerPreShape = """
        CREATE TABLE dbo.CALLER_ATTRIBUTES (
            id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NOT NULL, name NVARCHAR(128) NOT NULL, definition_json NVARCHAR(MAX) NOT NULL,
            definition_hash NVARCHAR(128) NOT NULL, status INT NOT NULL, created_by NVARCHAR(256) NOT NULL, approved_by NVARCHAR(256) NULL, approved_at NVARCHAR(64) NULL,
            draft_definition_json NVARCHAR(MAX) NULL, draft_definition_hash NVARCHAR(128) NULL, draft_created_by NVARCHAR(256) NULL,
            pending_deletion INT NOT NULL CONSTRAINT DF_CALLER_ATTRIBUTES_pending_deletion DEFAULT 0, deletion_requested_by NVARCHAR(256) NULL,
            updated_by NVARCHAR(256) NULL, updated_at NVARCHAR(64) NOT NULL);
        CREATE TABLE dbo.CALLER_ATTRIBUTE_SERVICE_SETS (
            id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NOT NULL, application_id NVARCHAR(256) NOT NULL, attribute_name NVARCHAR(128) NOT NULL,
            values_json NVARCHAR(MAX) NOT NULL, values_hash NVARCHAR(128) NOT NULL, status INT NOT NULL, created_by NVARCHAR(256) NOT NULL, approved_by NVARCHAR(256) NULL,
            approved_at NVARCHAR(64) NULL, draft_values_json NVARCHAR(MAX) NULL, draft_created_by NVARCHAR(256) NULL, updated_at NVARCHAR(64) NOT NULL);
        CREATE TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS (
            tenant_id NVARCHAR(64) NOT NULL, realm_id NVARCHAR(64) NOT NULL, group_or_role NVARCHAR(128) NOT NULL, functional_right NVARCHAR(64) NOT NULL,
            approved_by NVARCHAR(128) NULL, approved_at NVARCHAR(64) NULL, created_by NVARCHAR(128) NOT NULL);
        CREATE TABLE dbo.VIRTUAL_FILTERS (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NOT NULL, name NVARCHAR(128) NOT NULL, status NVARCHAR(32) NOT NULL DEFAULT N'Active');
        CREATE TABLE dbo.VIRTUAL_FILTER_ACCESS_PROFILES (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NOT NULL, name NVARCHAR(128) NOT NULL, definition_json NVARCHAR(MAX) NOT NULL);
        CREATE TABLE dbo.CALLER_ATTRIBUTE_GENERATION (id INT NOT NULL PRIMARY KEY, generation BIGINT NOT NULL);
        CREATE TABLE dbo.VIRTUAL_FILTER_GENERATION (id INT NOT NULL PRIMARY KEY, generation BIGINT NOT NULL);
        INSERT INTO dbo.CALLER_ATTRIBUTE_GENERATION VALUES (1, 5);
        INSERT INTO dbo.VIRTUAL_FILTER_GENERATION VALUES (1, 7);
        """;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Pending_items_inactive(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        await ExecAsync(db.Connection, $$"""
            INSERT INTO {{p}}CALLER_ATTRIBUTES (id, tenant_id, name, definition_json, definition_hash, status, created_by, approved_by, updated_at)
            VALUES ('a1', 't', 'approved', '{}', 'h', 0, 'alice', 'bob', 'x'), ('a2', 't', 'pending', '{}', 'h', 1, 'alice', NULL, 'x');
            INSERT INTO {{p}}CALLER_ATTRIBUTE_SERVICE_SETS (id, tenant_id, application_id, attribute_name, values_json, values_hash, status, created_by, approved_by, updated_at)
            VALUES ('s1', 't', 'app', 'approved', '[]', 'h', 0, 'alice', 'bob', 'x'), ('s2', 't', 'app', 'pending', '[]', 'h', 1, 'alice', NULL, 'x');
            INSERT INTO {{p}}FUNCTIONAL_RIGHT_MAPPINGS (tenant_id, realm_id, group_or_role, functional_right, approved_by, created_by)
            VALUES ('t', 'r', 'g1', 'Data.Read', 'bob', 'alice'), ('t', 'r', 'g2', 'Data.Read', NULL, 'alice');
            INSERT INTO {{p}}VIRTUAL_FILTERS (id, tenant_id, name, status) VALUES ('f1', 't', 'active_filter', 'Active'), ('f2', 't', 'pending_filter', 'PendingApproval');
            INSERT INTO {{p}}VIRTUAL_FILTER_ACCESS_PROFILES (id, tenant_id, name, definition_json)
            VALUES ('p1', 't', 'active_profile', '{"Status":0}'), ('p2', 't', 'pending_profile', '{"Status":1}');
            """);

        await RunStepAsync(db);

        (await ScalarStringAsync(db.Connection, $"SELECT name FROM {p}CALLER_ATTRIBUTES")).ShouldBe("approved");
        (await ScalarStringAsync(db.Connection, $"SELECT attribute_name FROM {p}CALLER_ATTRIBUTE_SERVICE_SETS")).ShouldBe("approved");
        (await ScalarStringAsync(db.Connection, $"SELECT group_or_role FROM {p}FUNCTIONAL_RIGHT_MAPPINGS")).ShouldBe("g1");
        (await ScalarStringAsync(db.Connection, $"SELECT name FROM {p}VIRTUAL_FILTERS")).ShouldBe("active_filter");
        (await ScalarStringAsync(db.Connection, $"SELECT name FROM {p}VIRTUAL_FILTER_ACCESS_PROFILES")).ShouldBe("active_profile");
        (await CountAsync(db.Connection, $"SELECT generation FROM {p}CALLER_ATTRIBUTE_GENERATION WHERE id = 1")).ShouldBe(6);
        (await CountAsync(db.Connection, $"SELECT generation FROM {p}VIRTUAL_FILTER_GENERATION WHERE id = 1")).ShouldBe(8);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Only_the_top_level_profile_status_decides(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        // An active profile whose nested draft is pending or rejected is a live grant restriction and must survive; only a profile
        // whose own (top-level) status is pending or rejected is discarded. A legacy row without status is active.
        await ExecAsync(db.Connection, $$$"""
            INSERT INTO {{{p}}}VIRTUAL_FILTER_ACCESS_PROFILES (id, tenant_id, name, definition_json) VALUES
            ('p1', 't', 'active_with_pending_draft', '{"GranteeType":1,"Bindings":[],"Status":0,"Draft":{"GranteeType":1,"Bindings":[],"Status":1}}'),
            ('p2', 't', 'active_with_rejected_draft', '{"GranteeType":1,"Bindings":[],"Status":0,"Draft":{"GranteeType":1,"Bindings":[],"Status":2}}'),
            ('p3', 't', 'legacy_without_status', '{"GranteeType":1,"Bindings":[],"Status":null}'),
            ('p4', 't', 'pending_with_draft', '{"GranteeType":1,"Bindings":[],"Status":1,"Draft":{"GranteeType":1,"Bindings":[],"Status":0}}'),
            ('p5', 't', 'rejected', '{"GranteeType":1,"Bindings":[],"Status":2,"Draft":null}'),
            ('p6', 't', 'status_text_in_binding', '{"GranteeType":1,"Bindings":[{"Filter":"x\"Status\":1"}],"Status":0}');
            """);

        await RunStepAsync(db);

        var survivors = await StringsAsync(db.Connection, $"SELECT name FROM {p}VIRTUAL_FILTER_ACCESS_PROFILES ORDER BY name");
        survivors.ShouldBe(["active_with_pending_draft", "active_with_rejected_draft", "legacy_without_status", "status_text_in_binding"]);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Draft_and_pending_deletion_columns_are_dropped(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        await RunStepAsync(db);

        foreach (var (table, column) in new[]
        {
            ("CALLER_ATTRIBUTES", "draft_definition_json"), ("CALLER_ATTRIBUTES", "draft_definition_hash"), ("CALLER_ATTRIBUTES", "draft_created_by"),
            ("CALLER_ATTRIBUTES", "pending_deletion"), ("CALLER_ATTRIBUTES", "deletion_requested_by"),
            ("CALLER_ATTRIBUTE_SERVICE_SETS", "draft_values_json"), ("CALLER_ATTRIBUTE_SERVICE_SETS", "draft_created_by")
        })
        {
            await Should.ThrowAsync<DbException>(() => ScalarStringAsync(db.Connection, $"SELECT {column} FROM {p}{table}"), $"{table}.{column} must be gone");
        }
    }

    private static async Task<Db?> OpenPreStepAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 43 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("single_approval_0043"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlPreShape);
            return new Db(engine, conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        await ExecAsync(sqlConn, SqlServerPreShape);

        return new Db(engine, sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static async Task RunStepAsync(Db db)
    {
        await using var tx = await db.Connection.BeginTransactionAsync();
        try
        {
            foreach (var batch in db.Step.Batches)
            {
                await ExecAsync(db.Connection, batch, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string[]> StringsAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new System.Collections.Generic.List<string>();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return [.. list];
    }

    private static async Task<string?> ScalarStringAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private sealed record Db(string Engine, DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
