using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Integration.OData;

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
using Autheris.Tests.Unit;
using Npgsql;
using Shouldly;

/// <summary>
/// PostgreSQL integration tests for OData $orderby and $count via <see cref="SqlTableReadExecutor"/>.
/// </summary>
public sealed class ODataOrderByCountIntegrationTests
{
    private static readonly TableIdentifier Invoices = new("erp", "public", "invoices");

    private static TableMetadata InvoiceMetadata() => new()
    {
        Identifier = Invoices,
        Table = new Table { SourceName = "erp", SchemaName = "public", TableName = "invoices", DataSourceType = DataSourceType.Sql, SourceType = "PostgreSql" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "region", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "iban", DataType = "text" }
        ]
    };

    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ColumnAccessLevel.Clear,
        ["region"] = ColumnAccessLevel.Clear,
        ["amount"] = ColumnAccessLevel.Clear,
        ["iban"] = ColumnAccessLevel.Mask
    };

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.invoices (id integer PRIMARY KEY, region text NOT NULL, amount integer NOT NULL, iban text);
            INSERT INTO public.invoices VALUES
                (1, 'CH', 300, 'CH01'),
                (2, 'DE', 100, 'DE02'),
                (3, 'CH', 200, 'CH03'),
                (4, 'CH', 50, 'CH04');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqlTableReadExecutor CreateExecutor(string connectionString)
    {
        var env = new PgCompileEnv()
            .Table("invoices", null, "id:integer", "region", "amount:integer", "iban")
            .Policy("invoices", PgGovernedSqlService.RowPolicy("region", "CH"));
        return new PgGovernedSqlService(connectionString, env).Executor();
    }

    private static DataSourceExecutionContext Context(int limit, int offset, IReadOnlyList<TableOrderBy>? orderBy, bool count)
    {
        var items = new Dictionary<string, object?>();
        if (orderBy != null) items[TableQueryItems.OrderBy] = orderBy;
        if (count) items[TableQueryItems.CountTotal] = true;
        return new DataSourceExecutionContext(
            SourceName: "erp",
            Metadata: InvoiceMetadata(),
            Principal: new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test")),
            AccessDecision: TableAccessDecision.Allowed(Invoices, Access, rowFilterSql: "region = 'CH'"),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "amount"],
            Limit: limit,
            Offset: offset,
            Items: items);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_OrdersAndCountsUnderTheRowFilter()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        var context = Context(limit: 2, offset: 0, [new TableOrderBy("amount", Descending: true)], count: true);

        var rows = await CreateExecutor(db.ConnectionString).ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L, 3L]);
        context.Items[TableQueryItems.TotalCount].ShouldBe(3L);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_SecondPage_FollowsTheSameOrder()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        var rows = await CreateExecutor(db.ConnectionString).ExecuteAsync(Context(limit: 2, offset: 2, [new TableOrderBy("amount")], count: false));

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([1L]);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_OrderingByMaskedColumn_IsRejected()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        await Should.ThrowAsync<ArgumentException>(() =>
            CreateExecutor(db.ConnectionString).ExecuteAsync(Context(limit: 2, offset: 0, [new TableOrderBy("iban")], count: false)));
    }
}
