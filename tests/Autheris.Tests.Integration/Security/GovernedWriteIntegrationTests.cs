namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Governed writes end to end on PostgreSQL and SQL Server containers: the write grant opens the governed path, the dedicated write
/// login holds column-level privileges only, and a statement outside the caller's rows touches nothing.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernedWriteIntegrationTests
{
    private const string Postgres = "PostgreSql";
    private const string SqlServer = "SqlServer";

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task Insert_Update_WithGrants_EndToEnd(string engine)
    {
        await using var env = await WriteEnvironment.CreateAsync(engine);
        if (env is null) return;

        var audit = new List<AuditLogEntry>();
        var service = env.Service(audit);

        await GovernedPipelineHarness.ExecuteAsync(service, $"INSERT INTO {env.QualifiedTable} (id, tenant_id, amount) VALUES (10, 'tenant_a', 42)");
        (await env.ScalarAsync("SELECT COUNT(*) FROM " + env.QualifiedTable + " WHERE id = 10 AND tenant_id = 'tenant_a' AND amount = 42")).ShouldBe(1);

        await GovernedPipelineHarness.ExecuteAsync(service, $"UPDATE {env.QualifiedTable} SET amount = 7 WHERE id = 10");
        (await env.ScalarAsync("SELECT COUNT(*) FROM " + env.QualifiedTable + " WHERE id = 10 AND amount = 7")).ShouldBe(1);

        audit.ShouldContain(e => e.EventType == "WEBSQL_DML_EXECUTED");
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task Update_OutsideOwnRows_AffectsZeroRows(string engine)
    {
        await using var env = await WriteEnvironment.CreateAsync(engine);
        if (env is null) return;

        var service = env.Service(new List<AuditLogEntry>());

        // Row 4 belongs to another tenant: the governed statement carries the caller's tenant predicate, so nothing matches.
        await GovernedPipelineHarness.ExecuteAsync(service, $"UPDATE {env.QualifiedTable} SET amount = 0 WHERE id = 4");

        (await env.ScalarAsync("SELECT amount FROM " + env.QualifiedTable + " WHERE id = 4")).ShouldBe(20);
        (await env.ScalarAsync("SELECT COUNT(*) FROM " + env.QualifiedTable + " WHERE amount = 0")).ShouldBe(0);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task WriteIdentity_HasNoPrivilegeOutsideGrants(string engine)
    {
        await using var env = await WriteEnvironment.CreateAsync(engine);
        if (env is null) return;

        // Inside the grants (column-level UPDATE of amount) the login works.
        await env.RawAsExecuteAsync($"UPDATE {env.QualifiedTable} SET amount = 11 WHERE id = 1");

        // Outside them every statement is refused by the database itself.
        foreach (var statement in new[]
                 {
                     $"DELETE FROM {env.QualifiedTable} WHERE id = 1",
                     $"UPDATE {env.QualifiedTable} SET tenant_id = 'tenant_b' WHERE id = 1",
                     $"SELECT amount FROM {env.QualifiedTable}",
                     $"SELECT id FROM {env.SecretTable}",
                     "CREATE TABLE core.created_by_write_identity (id int)"
                 })
        {
            (await env.RawAsIsDeniedAsync(statement)).ShouldBeTrue(statement);
        }

        (await env.ScalarAsync("SELECT COUNT(*) FROM " + env.QualifiedTable)).ShouldBe(4);
        (await env.ScalarAsync("SELECT amount FROM " + env.QualifiedTable + " WHERE id = 1")).ShouldBe(11);
    }

    private sealed class WriteEnvironment : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase? _pg;
        private readonly SqlServerTestDatabase? _ss;
        private readonly string _login = "autheris_w_" + Guid.NewGuid().ToString("N")[..8];
        private readonly string _password = "Aut!" + Guid.NewGuid().ToString("N");
        private string _admin = string.Empty;
        private string _write = string.Empty;

        private WriteEnvironment(string provider, PostgreSqlTestDatabase? pg, SqlServerTestDatabase? ss)
        {
            Provider = provider;
            _pg = pg;
            _ss = ss;
        }

        public string Provider { get; }

        public string Table => Provider == Postgres ? "orders" : "Orders";

        public string QualifiedTable => "core." + Table;

        public string SecretTable => "core." + (Provider == Postgres ? "secret" : "Secret");

        public static async Task<WriteEnvironment?> CreateAsync(string engine)
        {
            try
            {
                if (engine == Postgres)
                {
                    var pg = await PostgreSqlTestDatabase.CreateAsync();
                    if (!pg.IsAvailable)
                    {
                        await pg.DisposeAsync();
                        return null;
                    }

                    var env = new WriteEnvironment(engine, pg, null);
                    await env.SetUpPostgresAsync();
                    return env;
                }

                var ss = await SqlServerTestDatabase.CreateAsync();
                if (!ss.IsAvailable)
                {
                    await ss.DisposeAsync();
                    return null;
                }

                var sql = new WriteEnvironment(engine, null, ss);
                await sql.SetUpSqlServerAsync();
                return sql;
            }
            catch (Exception ex)
            {
                ContainerStartup.FailIfRequired(ex, "The " + engine + " governed write test database");
                return null;
            }
        }

        public Autheris.Application.Sql.Services.GovernedSqlExecutionService Service(List<AuditLogEntry> audit)
        {
            var orders = GovernedPipelineHarness.Orders(Provider, "core", Table);
            var consent = GovernedPipelineHarness.WriteConsent(
                orders,
                new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "amount", "tenant_id"], WriteRowScope.AllRows),
                new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount"], WriteRowScope.AllRows));
            return GovernedPipelineHarness.Create(Provider, _admin, _write, "core", orders, [consent], audit, allowDml: true);
        }

        private async Task SetUpPostgresAsync()
        {
            _admin = _pg!.ConnectionString;
            var database = new NpgsqlConnectionStringBuilder(_admin).Database!;
            await using var conn = new NpgsqlConnection(_admin);
            await conn.OpenAsync();
            await ExecAsync(conn, $"""
                CREATE SCHEMA core;
                CREATE TABLE core.orders (id integer, amount bigint, tenant_id text);
                INSERT INTO core.orders VALUES (1, 10, 'tenant_a'), (2, 10, 'tenant_a'), (3, 20, 'tenant_a'), (4, 20, 'tenant_b');
                CREATE TABLE core.secret (id integer);
                CREATE ROLE {_login} LOGIN PASSWORD '{_password}';
                GRANT CONNECT ON DATABASE "{database}" TO {_login};
                GRANT USAGE ON SCHEMA core TO {_login};
                GRANT SELECT (id, tenant_id) ON core.orders TO {_login};
                GRANT INSERT (id, amount, tenant_id) ON core.orders TO {_login};
                GRANT UPDATE (amount) ON core.orders TO {_login};
                """);
            _write = new NpgsqlConnectionStringBuilder(_admin) { Username = _login, Password = _password }.ConnectionString;
        }

        private async Task SetUpSqlServerAsync()
        {
            _admin = _ss!.ConnectionString;
            var master = new SqlConnectionStringBuilder(_admin) { InitialCatalog = "master" }.ConnectionString;
            await using (var m = new SqlConnection(master))
            {
                await m.OpenAsync();
                await ExecAsync(m, $"CREATE LOGIN [{_login}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF");
            }

            await using var conn = new SqlConnection(_admin);
            await conn.OpenAsync();
            foreach (var statement in new[]
                     {
                         "CREATE SCHEMA core",
                         "CREATE TABLE core.Orders (id int, amount bigint, tenant_id varchar(64))",
                         "INSERT INTO core.Orders VALUES (1, 10, 'tenant_a'), (2, 10, 'tenant_a'), (3, 20, 'tenant_a'), (4, 20, 'tenant_b')",
                         "CREATE TABLE core.Secret (id int)",
                         $"CREATE USER [{_login}] FOR LOGIN [{_login}]",
                         $"GRANT SELECT ON core.Orders (id, tenant_id) TO [{_login}]",
                         $"GRANT INSERT ON core.Orders TO [{_login}]",
                         $"GRANT UPDATE ON core.Orders (amount) TO [{_login}]"
                     })
            {
                await ExecAsync(conn, statement);
            }

            _write = new SqlConnectionStringBuilder(_admin) { UserID = _login, Password = _password, Pooling = false }.ConnectionString;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var conn = Open(_admin);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        /// <summary>Runs a statement as the write login and fails when it is not accepted.</summary>
        public async Task RawAsExecuteAsync(string sql)
        {
            await using var conn = Open(_write);
            await conn.OpenAsync();
            await ExecAsync(conn, sql);
        }

        /// <summary>True when the database refuses the statement for the write login because of a missing privilege.</summary>
        public async Task<bool> RawAsIsDeniedAsync(string sql)
        {
            try
            {
                await using var conn = Open(_write);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync();
                return false;
            }
            catch (PostgresException ex)
            {
                return ex.SqlState == PostgresErrorCodes.InsufficientPrivilege;
            }
            catch (SqlException ex)
            {
                // 229 object permission denied, 230 column permission denied, 262 CREATE TABLE denied, 2760 schema missing or no permission.
                return ex.Number is 229 or 230 or 262 or 2760;
            }
        }

        private DbConnection Open(string connectionString) =>
            Provider == Postgres ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);

        private static async Task ExecAsync(DbConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_pg is not null)
                {
                    NpgsqlConnection.ClearAllPools();
                    await using (var conn = new NpgsqlConnection(_admin))
                    {
                        await conn.OpenAsync();
                        await ExecAsync(conn, $"DROP OWNED BY {_login}");
                    }

                    await _pg.DisposeAsync();
                    var server = new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres" }.ConnectionString;
                    await using var admin = new NpgsqlConnection(server);
                    await admin.OpenAsync();
                    await ExecAsync(admin, $"DROP ROLE IF EXISTS {_login}");
                }

                if (_ss is not null)
                {
                    SqlConnection.ClearAllPools();
                    await _ss.DisposeAsync();
                    var server = new SqlConnectionStringBuilder(_admin) { InitialCatalog = "master" }.ConnectionString;
                    await using var cleanup = new SqlConnection(server);
                    await cleanup.OpenAsync();
                    await ExecAsync(cleanup, $"IF SUSER_ID(N'{_login}') IS NOT NULL DROP LOGIN [{_login}]");
                }
            }
            catch (Exception)
            {
                // Best effort: the shared containers are removed at process exit anyway.
            }
        }
    }
}
