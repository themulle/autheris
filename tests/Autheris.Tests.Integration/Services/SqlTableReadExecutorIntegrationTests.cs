namespace Autheris.Tests.Integration.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.TestDoubles;
using Npgsql;
using Shouldly;

/// <summary>
/// PostgreSQL integration tests for <see cref="SqlTableReadExecutor"/> row-policy and column-mask pushdown.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlTableReadExecutorIntegrationTests
{
    private static readonly TableIdentifier Orders = new("shop", "public", "orders");

    private static TableMetadata Metadata() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "shop", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, SourceType = "PostgreSql", IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "status", DataType = "text" },
            new TableColumn { ColumnName = "email", DataType = "text" },
            new TableColumn { ColumnName = "shape", DataType = "text" }
        ]
    };

    private static TableAccessDecision Decision() => TableAccessDecision.Allowed(
        Orders,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["status"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask,
            ["shape"] = ColumnAccessLevel.Clear
        },
        hasUnconstrainedColumnAllow: true);

    [RequiresContainerRuntimeFact]
    public async Task Execute_OverPostgreSql_AppliesTheCompilerRowPolicyAndMask()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var setup = conn.CreateCommand();
            setup.CommandText = """
                CREATE TABLE public.orders (id integer PRIMARY KEY, status text, email text, shape text);
                INSERT INTO public.orders VALUES (1, 'open', 'a@x.example', NULL), (2, 'closed', 'b@x.example', NULL), (3, 'open', 'c@x.example', NULL);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var env = new PgCompileEnv()
            .Table("orders", null, "id:integer", "status", "email", "shape")
            .Policy("orders", PgGovernedSqlService.RowPolicy("status", "open"))
            .Mask("orders", "email", PgGovernedSqlService.Redact("email", "***"));
        var executor = new PgGovernedSqlService(db.ConnectionString, env).Executor();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-EXEC"), new Claim("tenant_id", "t1")], "Test"));
        var context = new DataSourceExecutionContext(
            SourceName: "shop",
            Metadata: Metadata(),
            Principal: principal,
            AccessDecision: Decision(),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "email"],
            Limit: 10,
            Offset: 0,
            Tenant: new TenantId("t1"),
            Items: new Dictionary<string, object?> { [TableQueryItems.CountTotal] = true });

        var rows = await executor.ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L, 3L]);
        rows.ShouldAllBe(r => (string?)r["email"] == "***");
        context.Items[TableQueryItems.TotalCount].ShouldBe(2L);
        context.Attestation.InDbColumnMaskingExecuted.ShouldBeTrue();
    }
}
