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

/// <summary>
/// Step 0045 (<c>drop_table_ownership</c>, #1009 W3-12b, D13, SEC-1009-48): the per-table ownership tables are dropped without conversion,
/// owner grants are revoked (other grants stay untouched), the epochs are bumped, and a re-run is a no-op.
/// </summary>
[Trait("Category", "Security")]
public sealed class DropTableOwnershipMigrationStep0045Tests
{
    private const string StepName = "drop_table_ownership";

    private const string PostgreSqlPreShape = """
        CREATE SCHEMA pre_step_0045;
        SET search_path TO pre_step_0045;
        CREATE TABLE DATA_OWNERS (id TEXT PRIMARY KEY, ad_sid TEXT NOT NULL, ad_account TEXT NOT NULL, display_name TEXT NOT NULL, email TEXT NOT NULL, is_active INTEGER NOT NULL);
        CREATE TABLE TABLE_OWNERS (id TEXT PRIMARY KEY, table_id TEXT NOT NULL, data_owner_id TEXT NOT NULL, owner_role TEXT NOT NULL);
        CREATE TABLE DATA_OWNER_DELEGATIONS (id TEXT PRIMARY KEY, data_owner_id TEXT NOT NULL, delegate_sid TEXT NOT NULL, valid_from TEXT NOT NULL, valid_to TEXT NOT NULL, reason TEXT NOT NULL);
        CREATE TABLE CONSENTS (id TEXT PRIMARY KEY, grant_source VARCHAR(24) NOT NULL, is_revoked INTEGER NOT NULL, revoked_by_sid TEXT, revoked_at TEXT, revoke_reason TEXT);
        CREATE TABLE governance_epochs (tenant_id VARCHAR(128) PRIMARY KEY, epoch BIGINT NOT NULL, updated_at TIMESTAMPTZ NOT NULL);
        CREATE TABLE POLICY_EPOCHS (table_id TEXT PRIMARY KEY, epoch BIGINT NOT NULL);
        INSERT INTO governance_epochs VALUES ('tenant-a', 3, CURRENT_TIMESTAMP);
        INSERT INTO POLICY_EPOCHS VALUES ('t1', 5);
        """;

    private const string SqlServerPreShape = """
        CREATE TABLE dbo.DATA_OWNERS (id NVARCHAR(128) NOT NULL PRIMARY KEY, ad_sid NVARCHAR(256) NOT NULL, ad_account NVARCHAR(256) NOT NULL, display_name NVARCHAR(512) NOT NULL, email NVARCHAR(512) NOT NULL, is_active INT NOT NULL);
        CREATE TABLE dbo.TABLE_OWNERS (id NVARCHAR(128) NOT NULL PRIMARY KEY, table_id NVARCHAR(128) NOT NULL, data_owner_id NVARCHAR(128) NOT NULL, owner_role NVARCHAR(64) NOT NULL);
        CREATE TABLE dbo.DATA_OWNER_DELEGATIONS (id NVARCHAR(128) NOT NULL PRIMARY KEY, data_owner_id NVARCHAR(128) NOT NULL, delegate_sid NVARCHAR(256) NOT NULL, valid_from NVARCHAR(64) NOT NULL, valid_to NVARCHAR(64) NOT NULL, reason NVARCHAR(MAX) NOT NULL);
        CREATE TABLE dbo.CONSENTS (id NVARCHAR(128) NOT NULL PRIMARY KEY, grant_source NVARCHAR(24) NOT NULL, is_revoked INT NOT NULL, revoked_by_sid NVARCHAR(256) NULL, revoked_at NVARCHAR(64) NULL, revoke_reason NVARCHAR(MAX) NULL);
        CREATE TABLE dbo.GOVERNANCE_EPOCHS (tenant_id NVARCHAR(128) NOT NULL PRIMARY KEY, epoch BIGINT NOT NULL, updated_at DATETIMEOFFSET NOT NULL);
        CREATE TABLE dbo.POLICY_EPOCHS (table_id NVARCHAR(128) NOT NULL PRIMARY KEY, epoch BIGINT NOT NULL);
        INSERT INTO dbo.GOVERNANCE_EPOCHS VALUES (N'tenant-a', 3, SYSDATETIMEOFFSET());
        INSERT INTO dbo.POLICY_EPOCHS VALUES (N't1', 5);
        """;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Ownership_tables_are_dropped_and_owner_grants_revoked(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        await ExecAsync(db.Connection, $"""
            INSERT INTO {p}DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES ('o1', 'S-1-5-21-O1', 'CORP\o1', 'O1', 'o1@x.test', 1);
            INSERT INTO {p}TABLE_OWNERS (id, table_id, data_owner_id, owner_role) VALUES ('to1', 't1', 'o1', 'PRIMARY');
            INSERT INTO {p}DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason) VALUES ('d1', 'o1', 'S-1-5-21-D1', 'a', 'b', 'r');
            INSERT INTO {p}CONSENTS (id, grant_source, is_revoked) VALUES ('c-owner', 'OWNER', 0), ('c-request', 'CONSENT_REQUEST', 0), ('c-old-owner', 'OWNER', 1);
            """);

        await RunStepAsync(db);

        foreach (var table in new[] { "DATA_OWNER_DELEGATIONS", "TABLE_OWNERS", "DATA_OWNERS" })
        {
            await Should.ThrowAsync<DbException>(() => CountAsync(db.Connection, $"SELECT COUNT(*) FROM {p}{table}"), $"{table} must be dropped");
        }

        (await CountAsync(db.Connection, $"SELECT is_revoked FROM {p}CONSENTS WHERE id = 'c-owner'")).ShouldBe(1, "an owner grant ends with the ownership model");
        (await ScalarStringAsync(db.Connection, $"SELECT revoke_reason FROM {p}CONSENTS WHERE id = 'c-owner'")).ShouldBe("D13_TABLE_OWNERSHIP_REMOVED");
        (await ScalarStringAsync(db.Connection, $"SELECT revoked_by_sid FROM {p}CONSENTS WHERE id = 'c-owner'")).ShouldBe("system:migration-0045");
        (await CountAsync(db.Connection, $"SELECT is_revoked FROM {p}CONSENTS WHERE id = 'c-request'")).ShouldBe(0, "other grants are untouched");
        (await ScalarStringAsync(db.Connection, $"SELECT revoke_reason FROM {p}CONSENTS WHERE id = 'c-old-owner'")).ShouldBeNull("an already revoked grant keeps its revocation");
        (await CountAsync(db.Connection, $"SELECT epoch FROM {p}GOVERNANCE_EPOCHS WHERE tenant_id = 'tenant-a'")).ShouldBe(4);
        (await CountAsync(db.Connection, $"SELECT epoch FROM {p}POLICY_EPOCHS WHERE table_id = 't1'")).ShouldBe(6);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Rerun_after_the_tables_are_gone_is_a_no_op(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        await RunStepAsync(db);
        await RunStepAsync(db);

        (await CountAsync(db.Connection, $"SELECT COUNT(*) FROM {p}CONSENTS")).ShouldBe(0);
    }

    private static async Task<Db?> OpenPreStepAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 45 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("drop_table_ownership_0045"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlPreShape);
            return new Db(conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        await ExecAsync(sqlConn, SqlServerPreShape);

        return new Db(sqlConn, step, async () =>
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

    private static async Task<string?> ScalarStringAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private sealed record Db(DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
