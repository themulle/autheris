namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// The governed read paths work under the source role of <c>docs/operations/source-least-privilege-roles.md</c>, created with exactly the
/// documented grants on PostgreSQL and SQL Server containers, and Autheris never issues a catalog statement while doing so.
/// <para>
/// Catalog trace: SQL Server uses an Extended Events ring buffer filtered to the service login and read by the admin connection (no
/// container access needed). PostgreSQL sets <c>log_statement = 'all'</c> on the service role only (so the server log holds just its
/// statements) and reads the container log; with an external server (<c>AUTHERIS_TEST_POSTGRES</c>) the log is unreachable and that
/// case is skipped.
/// </para>
/// </summary>
[Trait("Category", "Security")]
public sealed class SourceLeastPrivilegeContainerTests
{
    private const string Postgres = "PostgreSql";
    private const string SqlServer = "SqlServer";

    // Any statement that reads a system catalog on behalf of Autheris; the documented session setter is the only sys.* call allowed.
    private static readonly Regex CatalogStatement = new(
        @"\b(sys\.(?!sp_set_session_context)|information_schema\.|pg_catalog|pg_type\b|pg_class\b|pg_namespace\b|pg_attribute\b|pg_proc\b|pg_index\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task GovernedRead_Works_UnderTemplateRole(string engine)
    {
        await using var env = await TemplateRoleEnvironment.CreateAsync(engine);
        if (env is null) return;

        await RunGovernedReadAsync(env);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task ProcedureCall_Works_UnderTemplateRole(string engine)
    {
        await using var env = await TemplateRoleEnvironment.CreateAsync(engine);
        if (env is null) return;

        await RunProcedureCallAsync(env);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task RowScopeClosure_Works_UnderTemplateRole(string engine)
    {
        await using var env = await TemplateRoleEnvironment.CreateAsync(engine);
        if (env is null) return;

        await RunRowScopeClosureAsync(env);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlServer_SysCatalogSelect_Denied()
    {
        await using var env = await TemplateRoleEnvironment.CreateAsync(SqlServer);
        if (env is null) return;

        await using var conn = new SqlConnection(env.ServiceConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.tables";
        var ex = await Should.ThrowAsync<SqlException>(async () => await cmd.ExecuteScalarAsync());
        ex.Number.ShouldBe(229);

        // The granted object itself stays readable, so the denial is the role's, not a broken login.
        cmd.CommandText = "SELECT COUNT(*) FROM core.Orders";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).ShouldBeGreaterThan(0);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData(Postgres)]
    [InlineData(SqlServer)]
    public async Task AutherisRequestTrace_ContainsNoCatalogStatement(string engine)
    {
        await using var env = await TemplateRoleEnvironment.CreateAsync(engine);
        if (env is null) return;

        var statements = await env.TraceAsync(async () =>
        {
            await RunGovernedReadAsync(env);
            await RunProcedureCallAsync(env);
            await RunRowScopeClosureAsync(env);
        });
        if (statements is null) return; // PostgreSQL on an external server: no log access.

        statements.ShouldNotBeEmpty("the trace must have captured the statements of the service login");
        statements.Any(s => s.Contains("orders", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("the governed read must appear in the trace");
        statements.Where(s => CatalogStatement.IsMatch(s)).ShouldBeEmpty("Autheris must not issue a catalog statement");
    }

    private static async Task RunGovernedReadAsync(TemplateRoleEnvironment env)
    {
        var orders = GovernedPipelineHarness.Orders(env.Provider, "core", env.OrdersTable);
        var service = GovernedPipelineHarness.Create(env.Provider, env.ServiceConnectionString, null, "core", orders, [], new List<AuditLogEntry>(), allowDml: false);

        var result = await GovernedPipelineHarness.QueryAsync(service, "SELECT id, amount FROM " + env.OrdersTable);

        // Three rows belong to the caller's tenant; the fourth belongs to another tenant and is filtered by the governed query.
        result.RowCount.ShouldBe(3);
    }

    private static async Task RunProcedureCallAsync(TemplateRoleEnvironment env)
    {
        var factory = new SqlConnectionFactory(new DevelopmentEnvironment());
        await using var conn = await factory.CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = env.Provider, ConnectionString = env.ServiceConnectionString, DefaultSchema = "core" });
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = env.Provider == Postgres ? "SELECT id, amount FROM core.fn_orders_for(1)" : "EXEC core.usp_Orders";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
        }

        rows.ShouldBeGreaterThan(0);
    }

    private static async Task RunRowScopeClosureAsync(TemplateRoleEnvironment env)
    {
        var resolver = new SqlProcedureRowScopeResolver(
            new SqlConnectionFactory(new DevelopmentEnvironment()),
            Options.Create(new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["erp"] = new() { Provider = env.Provider, ConnectionString = env.ServiceConnectionString, DefaultSchema = "core" }
                    }
                }
            }));
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "core", env.OrdersTable),
            Table = new Table { SourceName = "erp", SchemaName = "core", TableName = env.OrdersTable, SourceType = env.Provider, DataSourceType = DataSourceType.Sql },
            Columns = new[] { "id", "amount", "tenant_id" }.Select(c => new TableColumn { ColumnName = c }).ToList()
        };
        var definition = ProcedureDefinitionParser.ParseYaml(
            "name: p\nprocedure: core.usp_Orders\nresult_table: core." + env.OrdersTable + "\nrow_scope_key: amount", "x", false, 60) with { DataSource = "erp" };
        var decision = TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);

        // amount is not unique: 10 spans two rows of the caller's tenant (closed), 20 also holds a row of another tenant (never admitted).
        var allowed = await resolver.GetAllowedKeysAsync(
            definition, table, decision, ["amount"], [[10L], [20L], [30L]],
            new ProcedureSecurityContext("tenant_a", "S-1-5-21-1", null), CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize([10L])! });
    }

    /// <summary>A database with the documented template role: the statements are the ones of the operations document, per engine.</summary>
    private sealed class TemplateRoleEnvironment : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase? _pg;
        private readonly SqlServerTestDatabase? _ss;
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly string _login;
        private readonly string _password = "Aut!" + Guid.NewGuid().ToString("N");
        private string? _traceSession;
        private string _adminConnectionString = string.Empty;

        private TemplateRoleEnvironment(string provider, PostgreSqlTestDatabase? pg, SqlServerTestDatabase? ss)
        {
            Provider = provider;
            _pg = pg;
            _ss = ss;
            _login = "autheris_svc_" + _suffix;
        }

        public string Provider { get; }

        public string OrdersTable => Provider == Postgres ? "orders" : "Orders";

        public string ServiceConnectionString { get; private set; } = string.Empty;

        public static async Task<TemplateRoleEnvironment?> CreateAsync(string engine)
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

                    var env = new TemplateRoleEnvironment(engine, pg, null);
                    await env.SetUpPostgresAsync();
                    return env;
                }
                else
                {
                    var ss = await SqlServerTestDatabase.CreateAsync();
                    if (!ss.IsAvailable)
                    {
                        await ss.DisposeAsync();
                        return null;
                    }

                    var env = new TemplateRoleEnvironment(engine, null, ss);
                    await env.SetUpSqlServerAsync();
                    return env;
                }
            }
            catch (Exception ex)
            {
                ContainerStartup.FailIfRequired(ex, "The " + engine + " source role test database");
                return null;
            }
        }

        private async Task SetUpPostgresAsync()
        {
            _adminConnectionString = _pg!.ConnectionString;
            var database = new NpgsqlConnectionStringBuilder(_adminConnectionString).Database!;
            var data = "autheris_data_" + _suffix;
            await using (var conn = new NpgsqlConnection(_adminConnectionString))
            {
                await conn.OpenAsync();
                // Fixture objects (owned by the admin), then the documented template role statements verbatim (names suffixed for the shared server).
                await ExecAsync(conn, """
                    CREATE SCHEMA core;
                    CREATE TABLE core.orders (id integer, amount bigint, tenant_id text);
                    INSERT INTO core.orders VALUES (1, 10, 'tenant_a'), (2, 10, 'tenant_a'), (3, 20, 'tenant_a'), (4, 20, 'tenant_b');
                    CREATE VIEW core.v_orders AS SELECT id, amount, tenant_id FROM core.orders;
                    CREATE FUNCTION core.fn_orders_for(integer) RETURNS TABLE (id integer, amount bigint) LANGUAGE sql STABLE
                        AS $$ SELECT o.id, o.amount FROM core.orders o WHERE o.id = $1 $$;
                    """);
                await ExecAsync(conn, $"""
                    CREATE ROLE {data} NOLOGIN;
                    GRANT CONNECT ON DATABASE "{database}" TO {data};
                    GRANT USAGE ON SCHEMA core TO {data};
                    GRANT SELECT ON core.orders, core.v_orders TO {data};
                    GRANT EXECUTE ON FUNCTION core.fn_orders_for(integer) TO {data};
                    CREATE ROLE {_login} LOGIN PASSWORD '{_password}' IN ROLE {data};
                    ALTER ROLE {_login} SET default_transaction_read_only = on;
                    """);
            }

            ServiceConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Username = _login, Password = _password }.ConnectionString;
        }

        private async Task SetUpSqlServerAsync()
        {
            _adminConnectionString = _ss!.ConnectionString;
            var server = new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = "master" }.ConnectionString;
            await using (var master = new SqlConnection(server))
            {
                await master.OpenAsync();
                await ExecAsync(master, $"CREATE LOGIN [{_login}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF");
            }

            await using var conn = new SqlConnection(_adminConnectionString);
            await conn.OpenAsync();
            await ExecAsync(conn, "CREATE SCHEMA core");
            await ExecAsync(conn, "CREATE TABLE core.Orders (id int, amount bigint, tenant_id varchar(64))");
            await ExecAsync(conn, "INSERT INTO core.Orders VALUES (1, 10, 'tenant_a'), (2, 10, 'tenant_a'), (3, 20, 'tenant_a'), (4, 20, 'tenant_b')");
            await ExecAsync(conn, "CREATE PROCEDURE core.usp_Orders AS BEGIN SELECT id, amount FROM core.Orders END");

            // The documented template role statements, verbatim.
            foreach (var statement in new[]
                     {
                         $"CREATE USER [{_login}] FOR LOGIN [{_login}]",
                         "CREATE ROLE autheris_data",
                         "GRANT SELECT ON OBJECT::core.Orders TO autheris_data",
                         "GRANT EXECUTE ON OBJECT::core.usp_Orders TO autheris_data",
                         $"ALTER ROLE autheris_data ADD MEMBER [{_login}]",
                         "DENY VIEW DEFINITION TO autheris_data",
                         "DENY VIEW DATABASE STATE TO autheris_data",
                         "DENY SELECT ON SCHEMA::sys TO autheris_data",
                         "DENY SELECT ON SCHEMA::INFORMATION_SCHEMA TO autheris_data"
                     })
            {
                await ExecAsync(conn, statement);
            }

            ServiceConnectionString = new SqlConnectionStringBuilder(_adminConnectionString) { UserID = _login, Password = _password, Pooling = false }.ConnectionString;
        }

        /// <summary>Runs <paramref name="work"/> under a statement trace of the service login and returns its statements (null when the engine log is unreachable).</summary>
        public async Task<IReadOnlyList<string>?> TraceAsync(Func<Task> work)
        {
            if (Provider == Postgres)
            {
                var since = DateTime.UtcNow.AddSeconds(-1);
                await using (var admin = new NpgsqlConnection(_adminConnectionString))
                {
                    await admin.OpenAsync();
                    await ExecAsync(admin, $"ALTER ROLE {_login} SET log_statement = 'all'");
                }

                await work();
                IReadOnlyList<string>? captured = [];
                var unreachable = false;
                await WaitHelper.WaitUntil(async () =>
                {
                    var log = await SharedDatabaseServers.PostgreSqlLogsSinceAsync(since);
                    if (log is null)
                    {
                        unreachable = true;
                        return true;
                    }

                    captured = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.Contains("statement:", StringComparison.Ordinal) || l.Contains("execute ", StringComparison.Ordinal))
                        .ToList();
                    return captured.Any(l => l.Contains("orders", StringComparison.OrdinalIgnoreCase));
                }, TimeSpan.FromSeconds(10), "the PostgreSQL log must show the service role statements", TimeSpan.FromMilliseconds(500));
                return unreachable ? null : captured;
            }

            _traceSession = "autheris_trace_" + _suffix;
            var predicate = $"sqlserver.server_principal_name = N'{_login}'";
            await using (var admin = new SqlConnection(_adminConnectionString))
            {
                await admin.OpenAsync();
                await ExecAsync(admin, $"""
                    CREATE EVENT SESSION [{_traceSession}] ON SERVER
                    ADD EVENT sqlserver.sql_batch_completed (ACTION (sqlserver.sql_text) WHERE {predicate}),
                    ADD EVENT sqlserver.rpc_completed (ACTION (sqlserver.sql_text) WHERE {predicate}),
                    ADD EVENT sqlserver.sql_statement_completed (ACTION (sqlserver.sql_text) WHERE {predicate})
                    ADD TARGET package0.ring_buffer (SET max_memory = 8192)
                    WITH (EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY = 1 SECONDS)
                    """);
                await ExecAsync(admin, $"ALTER EVENT SESSION [{_traceSession}] ON SERVER STATE = START");
            }

            await work();
            IReadOnlyList<string> statements = [];
            await WaitHelper.WaitUntil(async () =>
            {
                statements = await ReadTraceAsync();
                return statements.Any(x => x.Contains("orders", StringComparison.OrdinalIgnoreCase));
            }, TimeSpan.FromSeconds(15), "the Extended Events trace must show the service login statements", TimeSpan.FromMilliseconds(500));
            return statements;
        }

        private async Task<IReadOnlyList<string>> ReadTraceAsync()
        {
            await using var admin = new SqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = """
                SELECT CAST(t.target_data AS nvarchar(max))
                FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
                WHERE s.name = @name AND t.target_name = 'ring_buffer'
                """;
            cmd.Parameters.AddWithValue("@name", _traceSession);
            var xml = (await cmd.ExecuteScalarAsync()) as string;
            if (string.IsNullOrEmpty(xml))
            {
                return [];
            }

            return XDocument.Parse(xml).Descendants("event")
                .SelectMany(e => e.Descendants("value").Select(v => v.Value))
                .Where(v => v.Length > 0)
                .ToList();
        }

        private static async Task ExecAsync(System.Data.Common.DbConnection conn, string sql)
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
                    var data = "autheris_data_" + _suffix;
                    NpgsqlConnection.ClearAllPools();
                    await using (var conn = new NpgsqlConnection(_adminConnectionString))
                    {
                        await conn.OpenAsync();
                        await ExecAsync(conn, $"DROP OWNED BY {_login}, {data}");
                    }

                    await _pg.DisposeAsync();
                    var server = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" }.ConnectionString;
                    await using var admin = new NpgsqlConnection(server);
                    await admin.OpenAsync();
                    await ExecAsync(admin, $"DROP ROLE IF EXISTS {_login}; DROP ROLE IF EXISTS {data};");
                }

                if (_ss is not null)
                {
                    var server = new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = "master" }.ConnectionString;
                    await using (var admin = new SqlConnection(server))
                    {
                        await admin.OpenAsync();
                        if (_traceSession is not null)
                        {
                            await ExecAsync(admin, $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{_traceSession}') DROP EVENT SESSION [{_traceSession}] ON SERVER");
                        }
                    }

                    await _ss.DisposeAsync();
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
