using System.Data.Common;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

/// <summary>
/// Step 0029 (role grantee removal) on PostgreSQL and SQL Server: an active role grantee aborts the step with the row id and changes
/// nothing, revoked role rows are removed child rows first, both columns are dropped, and a re-run is a no-op.
/// </summary>
[Trait("Category", "Security")]
public sealed class RoleGranteeRemovalMigration0029Tests
{
    private const string StepName = "drop_role_grantee";
    private const string RoleNameText = "AnalystsSecretRoleName";
    private const string RoleProfileJson = "{\"GranteeType\":3,\"GranteeSid\":null,\"Scope\":\"a.b.c\",\"Uncovered\":0,\"Bindings\":[]}";
    private const string GroupProfileJson = "{\"GranteeType\":2,\"GranteeSid\":\"S-1-5-21-G\",\"Scope\":\"a.b.c\",\"Uncovered\":0,\"Bindings\":[]}";

    private const string PostgreSqlShape = """
        CREATE SCHEMA pre_step;
        SET search_path TO pre_step;
        CREATE TABLE consents (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, grantee_type TEXT NOT NULL, grantee_sid TEXT NULL, role_id TEXT NULL, role_name TEXT NULL, is_revoked INTEGER NOT NULL);
        CREATE INDEX ix_consents_tenant_role ON consents (tenant_id, role_name);
        CREATE TABLE consent_column_rules (id TEXT PRIMARY KEY, consent_id TEXT NOT NULL);
        CREATE TABLE consent_row_filters (id TEXT PRIMARY KEY, consent_id TEXT NOT NULL);
        CREATE TABLE consent_row_scopes (consent_id TEXT NOT NULL REFERENCES consents (id) ON DELETE CASCADE, entry_no INTEGER NOT NULL, PRIMARY KEY (consent_id, entry_no));
        CREATE TABLE virtual_filter_access_profiles (id TEXT PRIMARY KEY, definition_json TEXT NOT NULL);
        CREATE TABLE access_profile_assignments (profile_id TEXT NOT NULL, subject_type TEXT NOT NULL, deactivated_reason TEXT NULL);
        """;

    private const string SqlServerShape = """
        CREATE TABLE dbo.CONSENTS (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NOT NULL, grantee_type NVARCHAR(64) NOT NULL, grantee_sid NVARCHAR(256) NULL, role_id NVARCHAR(128) NULL, role_name NVARCHAR(256) NULL, is_revoked INT NOT NULL);
        CREATE INDEX ix_consents_tenant_role ON dbo.CONSENTS (tenant_id, role_name);
        CREATE TABLE dbo.CONSENT_COLUMN_RULES (id NVARCHAR(128) NOT NULL PRIMARY KEY, consent_id NVARCHAR(128) NOT NULL);
        CREATE TABLE dbo.CONSENT_ROW_FILTERS (id NVARCHAR(128) NOT NULL PRIMARY KEY, consent_id NVARCHAR(128) NOT NULL);
        CREATE TABLE dbo.CONSENT_ROW_SCOPES (consent_id NVARCHAR(128) NOT NULL REFERENCES dbo.CONSENTS (id) ON DELETE CASCADE, entry_no INT NOT NULL, PRIMARY KEY (consent_id, entry_no));
        CREATE TABLE dbo.VIRTUAL_FILTER_ACCESS_PROFILES (id NVARCHAR(128) NOT NULL PRIMARY KEY, definition_json NVARCHAR(MAX) NOT NULL);
        CREATE TABLE dbo.ACCESS_PROFILE_ASSIGNMENTS (profile_id NVARCHAR(128) NOT NULL, subject_type NVARCHAR(32) NOT NULL, deactivated_reason NVARCHAR(64) NULL);
        """;

    private sealed record Db(string Engine, DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose);

    private static async Task<Db?> OpenAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 29 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("role_grantee_0029"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlShape);
            return new Db(engine, conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        foreach (var statement in SqlServerShape.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await ExecAsync(sqlConn, statement);
        }

        return new Db(engine, sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static async Task RunStepAsync(Db db)
    {
        foreach (var batch in db.Step.Batches)
        {
            await ExecAsync(db.Connection, batch);
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static string Consents(string engine) => engine == "postgresql" ? "consents" : "dbo.CONSENTS";

    private static string Table(string engine, string name) => engine == "postgresql" ? name : "dbo." + name.ToUpperInvariant();

    private static Task<long> ColumnCountAsync(Db db, string column) => ScalarAsync(db.Connection, db.Engine == "postgresql"
        ? $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'consents' AND column_name = '{column}'"
        : $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CONSENTS') AND name = N'{column}'");

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Security_Step0029_ActiveRoleConsent_AbortsWithoutChange(string engine)
    {
        var db = await OpenAsync(engine);
        if (db is null) return;
        await using var _ = new AsyncDisposer(db.Dispose);

        var c = db.Connection;
        await ExecAsync(c, $"INSERT INTO {Consents(engine)} (id, tenant_id, grantee_type, role_id, role_name, is_revoked) VALUES ('active-role-consent', 't', 'Role', NULL, '{RoleNameText}', 0)");
        await ExecAsync(c, $"INSERT INTO {Consents(engine)} (id, tenant_id, grantee_type, role_id, role_name, is_revoked) VALUES ('revoked-role-consent', 't', 'Role', NULL, '{RoleNameText}', 1)");

        var failure = await Should.ThrowAsync<DbException>(() => RunStepAsync(db));

        failure.Message.ShouldContain("MIG-0029");
        failure.Message.ShouldContain("active-role-consent");
        failure.Message.ShouldNotContain(RoleNameText, Case.Insensitive);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Consents(engine)}")).ShouldBe(2);
        (await ColumnCountAsync(db, "role_name")).ShouldBe(1);
        (await ColumnCountAsync(db, "role_id")).ShouldBe(1);
    }

    [Theory]
    [InlineData("postgresql", "profile")]
    [InlineData("sqlserver", "profile")]
    [InlineData("postgresql", "assignment")]
    [InlineData("sqlserver", "assignment")]
    public async Task Security_Step0029_ActiveRoleProfile_AbortsWithoutChange(string engine, string kind)
    {
        var db = await OpenAsync(engine);
        if (db is null) return;
        await using var _ = new AsyncDisposer(db.Dispose);

        var c = db.Connection;
        await ExecAsync(c, $"INSERT INTO {Consents(engine)} (id, tenant_id, grantee_type, grantee_sid, is_revoked) VALUES ('revoked-role-consent', 't', 'Role', NULL, 1)");
        if (kind == "profile")
        {
            await ExecAsync(c, $"INSERT INTO {Table(engine, "virtual_filter_access_profiles")} (id, definition_json) VALUES ('group-profile', '{GroupProfileJson}')");
            await ExecAsync(c, $"INSERT INTO {Table(engine, "virtual_filter_access_profiles")} (id, definition_json) VALUES ('role-profile', '{RoleProfileJson}')");
        }
        else
        {
            await ExecAsync(c, $"INSERT INTO {Table(engine, "access_profile_assignments")} (profile_id, subject_type, deactivated_reason) VALUES ('role-assignment-profile', 'Role', NULL)");
        }

        var failure = await Should.ThrowAsync<DbException>(() => RunStepAsync(db));

        failure.Message.ShouldContain("MIG-0029");
        failure.Message.ShouldContain(kind == "profile" ? "role-profile" : "role-assignment-profile");
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Consents(engine)}")).ShouldBe(1);
        (await ColumnCountAsync(db, "role_name")).ShouldBe(1);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Security_Step0029_RevokedRoleRows_RemovedAndColumnsDropped_IdempotentRerun(string engine)
    {
        var db = await OpenAsync(engine);
        if (db is null) return;
        await using var _ = new AsyncDisposer(db.Dispose);

        var c = db.Connection;
        await ExecAsync(c, $"INSERT INTO {Consents(engine)} (id, tenant_id, grantee_type, grantee_sid, role_name, is_revoked) VALUES ('revoked-role', 't', 'Role', NULL, '{RoleNameText}', 1)");
        await ExecAsync(c, $"INSERT INTO {Consents(engine)} (id, tenant_id, grantee_type, grantee_sid, is_revoked) VALUES ('active-group', 't', 'Group', 'S-1-5-21-G', 0)");
        await ExecAsync(c, $"INSERT INTO {Table(engine, "consent_column_rules")} (id, consent_id) VALUES ('rule-1', 'revoked-role')");
        await ExecAsync(c, $"INSERT INTO {Table(engine, "consent_column_rules")} (id, consent_id) VALUES ('rule-2', 'active-group')");
        await ExecAsync(c, $"INSERT INTO {Table(engine, "consent_row_filters")} (id, consent_id) VALUES ('filter-1', 'revoked-role')");
        await ExecAsync(c, $"INSERT INTO {Table(engine, "consent_row_scopes")} (consent_id, entry_no) VALUES ('revoked-role', 1)");
        await ExecAsync(c, $"INSERT INTO {Table(engine, "access_profile_assignments")} (profile_id, subject_type, deactivated_reason) VALUES ('old-role-assignment', 'Role', 'ADR020_ROLE_GRANTEE_UNSUPPORTED')");

        await RunStepAsync(db);

        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Consents(engine)} WHERE id = 'revoked-role'")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Consents(engine)} WHERE id = 'active-group'")).ShouldBe(1);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Table(engine, "consent_column_rules")} WHERE consent_id = 'revoked-role'")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Table(engine, "consent_column_rules")} WHERE consent_id = 'active-group'")).ShouldBe(1);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Table(engine, "consent_row_filters")}")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Table(engine, "consent_row_scopes")}")).ShouldBe(0);
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Table(engine, "access_profile_assignments")}")).ShouldBe(0);
        (await ColumnCountAsync(db, "role_name")).ShouldBe(0);
        (await ColumnCountAsync(db, "role_id")).ShouldBe(0);

        await RunStepAsync(db);   // idempotent: the second run finds nothing to do and changes nothing

        (await ScalarAsync(c, $"SELECT COUNT(*) FROM {Consents(engine)}")).ShouldBe(1);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Security_Step0029_ShapeJson_MatchesSqlOnBothProviders(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var step = steps.Single(s => s.Version == 29 && s.Name == StepName);
        var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 29, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
        var consents = shape.Tables.Single(t => string.Equals(t.Name, "consents", StringComparison.OrdinalIgnoreCase));

        consents.Columns.ShouldNotContain(col => string.Equals(col.Name, "role_id", StringComparison.OrdinalIgnoreCase));
        consents.Columns.ShouldNotContain(col => string.Equals(col.Name, "role_name", StringComparison.OrdinalIgnoreCase));
        consents.Indexes.ShouldNotContain(i => string.Equals(i.Name, "ix_consents_tenant_role", StringComparison.OrdinalIgnoreCase));
        foreach (var dropped in new[] { "role_id", "role_name", "ix_consents_tenant_role" })
        {
            step.Sql.ShouldContain(dropped);
        }

        step.Sql.ShouldContain("MIG-0029");
    }

    private sealed class AsyncDisposer(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
