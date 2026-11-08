namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>
/// Procedure invoker against a real SQL Server (WP-SIMPL-39, SEC-PROC-05 reference dialect): the read-only SESSION_CONTEXT drives a
/// security policy, a procedure cannot overwrite it, and (OD-44) the invoker adds no transaction. Needs Docker; without it the tests return early.
/// </summary>
public sealed class ProcedureSqlServerIntegrationTests : IAsyncLifetime
{
    private static readonly ProcedureSecurityContext Caller = new("tenant-a", "S-1-5-21-1", "analytics");
    private SqlServerTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await SqlServerTestDatabase.CreateAsync();
            _available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The SQL Server test container");
            return;
        }

        if (!_available)
        {
            return;
        }

        await Exec(
            "CREATE SCHEMA sec; ",
            "CREATE TABLE dbo.orders (id int NOT NULL, tenant_id nvarchar(64) NOT NULL, total int NOT NULL); " +
            "INSERT INTO dbo.orders VALUES (1, 'tenant-a', 10), (2, 'tenant-b', 20), (3, 'tenant-a', 30);",
            "CREATE FUNCTION sec.fn_tenant(@t nvarchar(64)) RETURNS TABLE WITH SCHEMABINDING AS " +
            "RETURN SELECT 1 AS ok WHERE @t = CAST(SESSION_CONTEXT(N'autheris.tenant_id') AS nvarchar(64));",
            "CREATE SECURITY POLICY sec.tenant_policy ADD FILTER PREDICATE sec.fn_tenant(tenant_id) ON dbo.orders WITH (STATE = ON);",
            "CREATE PROCEDURE dbo.usp_Orders AS SELECT id, total FROM dbo.orders ORDER BY id;",
            "CREATE PROCEDURE dbo.usp_Overwrite AS EXEC sys.sp_set_session_context @key = N'autheris.tenant_id', @value = N'tenant-b'; SELECT id FROM dbo.orders;",
            "CREATE TABLE dbo.audit_log (id int IDENTITY, note nvarchar(20));",
            "CREATE PROCEDURE dbo.usp_Write AS INSERT INTO dbo.audit_log (note) VALUES (N'x'); SELECT 1 AS done;");
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private async Task Exec(params string[] statements)
    {
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        foreach (string sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private ProcedureInvoker Invoker()
    {
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["procedures"] = new() { Provider = "SqlServer", ConnectionString = _db!.ConnectionString }
                }
            }
        });
        return new ProcedureInvoker(new ProcedureConnectionProvider(new SqlConnectionFactory(new DevelopmentEnvironment()), options), options);
    }

    private static ProcedureDefinition Definition(string procedure, string outputs) =>
        ProcedureDefinitionParser.ParseYaml(
            $"name: p\nprocedure: dbo.{procedure}\nvalidation: declared\nresult_table: dbo.orders\noutputs:\n{outputs}", "p", false, 60);

    private Task<RawProcedureResult> Run(ProcedureDefinition definition) =>
        Invoker().ExecuteReadAsync(definition, new Dictionary<string, object?>(), Caller, 100, CancellationToken.None);

    [Fact]
    public async Task SecurityPolicy_FiltersByReadOnlySessionContext()
    {
        if (!_available) return;

        var result = await Run(Definition("usp_Orders", "  - id\n  - total\n"));

        result.Rows.Count.ShouldBe(2); // tenant-a only; tenant-b's row is filtered by the policy
    }

    [Fact]
    public async Task SessionContext_NotOverwritableByProcedure()
    {
        if (!_available) return;

        // The key is written with @read_only = 1 before the call, so the procedure's own attempt fails.
        await Should.ThrowAsync<SqlException>(() => Run(Definition("usp_Overwrite", "  - id\n")));
    }

    [Fact]
    public async Task DmlInsideProcedure_RunsAsDeclared()
    {
        if (!_available) return;

        // OD-44: the invoker adds no transaction or read-only enforcement; the operator's EXECUTE-only login is the limit.
        await Run(Definition("usp_Write", "  - done\n"));

        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.audit_log";
        ((int)(await cmd.ExecuteScalarAsync())!).ShouldBe(1);
    }

    [Fact]
    public async Task ResultColumns_AreReturnedByNameForMasking()
    {
        if (!_available) return;

        var result = await Run(Definition("usp_Orders", "  - id\n  - total\n"));

        result.Columns.ShouldBe(["id", "total"]);
    }
}
