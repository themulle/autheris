namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Procedure invoker against a real PostgreSQL (WP-SIMPL-39, SEC-PROC-05): table-valued functions with transaction-local settings
/// feeding row-level security. The invoker's transaction is never committed. Needs Docker; without it the tests return early.
/// </summary>
public sealed class ProcedurePostgreSqlIntegrationTests : IAsyncLifetime
{
    private const string LoginPassword = "Proc-Login-1";
    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", "analytics");
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            _available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
            return;
        }

        if (!_available)
        {
            return;
        }

        // A superuser bypasses row-level security, so the invoker connects as a plain login.
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE ROLE proc_login LOGIN PASSWORD '{LoginPassword}';
CREATE TABLE public.orders (id integer NOT NULL, tenant_id text NOT NULL, total integer NOT NULL);
INSERT INTO public.orders VALUES (1, 'tenant-a', 10), (2, 'tenant-b', 20), (3, 'tenant-a', 30);
ALTER TABLE public.orders ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_policy ON public.orders USING (tenant_id = current_setting('autheris.tenant_id', true));
CREATE TABLE public.audit_log (note text);
GRANT SELECT ON public.orders TO proc_login;
GRANT INSERT, SELECT ON public.audit_log TO proc_login;
CREATE FUNCTION public.fn_orders() RETURNS TABLE (id integer, total integer) LANGUAGE sql STABLE AS $$ SELECT o.id, o.total FROM public.orders o ORDER BY o.id $$;
CREATE FUNCTION public.fn_write() RETURNS TABLE (done integer) LANGUAGE plpgsql VOLATILE AS $$ BEGIN INSERT INTO public.audit_log VALUES ('x'); RETURN QUERY SELECT 1; END $$;
GRANT EXECUTE ON FUNCTION public.fn_orders(), public.fn_write() TO proc_login;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private Task<RawProcedureResult> Run(string function, string outputs)
    {
        string connectionString = new NpgsqlConnectionStringBuilder(_db!.ConnectionString) { Username = "proc_login", Password = LoginPassword }.ConnectionString;
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["procedures"] = new() { Provider = "PostgreSql", ConnectionString = connectionString }
                }
            }
        });
        var invoker = new ProcedureInvoker(new ProcedureConnectionProvider(new SqlConnectionFactory(new DevelopmentEnvironment()), options), options);
        var definition = ProcedureDefinitionParser.ParseYaml(
            $"name: p\nprocedure: public.{function}\nkind: tvf\nvalidation: declared\nresult_table: public.orders\noutputs:\n{outputs}", "p", false, 60);
        return invoker.ExecuteReadAsync(definition, new Dictionary<string, object?>(), Caller, 100, CancellationToken.None);
    }

    [Fact]
    public async Task RlsPolicy_FiltersByTransactionLocalSettings()
    {
        if (!_available) return;

        var result = await Run("fn_orders", "  - id\n  - total\n");

        result.Rows.Count.ShouldBe(2); // tenant-a only
    }

    [Fact]
    public async Task WriteInsideFunction_NeverCommitted()
    {
        if (!_available) return;

        // OD-44: no Autheris-side read-only flag; the invoker's transaction is only disposed, so the write is rolled back.
        var result = await Run("fn_write", "  - done\n");
        result.Rows.Count.ShouldBe(1);

        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM public.audit_log";
        ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(0);
    }

    [Fact]
    public async Task ResultColumns_AreReturnedByName()
    {
        if (!_available) return;

        var result = await Run("fn_orders", "  - id\n  - total\n");

        result.Columns.ShouldBe(["id", "total"]);
    }
}
