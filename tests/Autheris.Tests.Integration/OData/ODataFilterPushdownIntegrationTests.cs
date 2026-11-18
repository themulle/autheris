using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Integration.OData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.OData;
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
/// PostgreSQL integration tests for OData $filter pushdown via <see cref="SqlTableReadExecutor"/>.
/// </summary>
public sealed class ODataFilterPushdownIntegrationTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static TableMetadata OrderMetadata() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "sales", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, SourceType = "PostgreSql" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "status", DataType = "text" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "card_number", DataType = "text" }
        ]
    };

    private static readonly Dictionary<string, ColumnAccessLevel> Access = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ColumnAccessLevel.Clear,
        ["status"] = ColumnAccessLevel.Clear,
        ["amount"] = ColumnAccessLevel.Clear,
        ["card_number"] = ColumnAccessLevel.Mask
    };

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.orders (id integer PRIMARY KEY, status text NOT NULL, amount integer NOT NULL, card_number text);
            INSERT INTO public.orders VALUES
                (1, 'pending', 100, '4111-1111'),
                (2, 'completed', 250, '4222-2222'),
                (3, 'completed', 500, '4333-3333'),
                (4, 'cancelled', 50, '4444-4444');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqlTableReadExecutor CreateExecutor(string connectionString)
    {
        var env = new PgCompileEnv()
            .Table("orders", null, "id:integer", "status", "amount:integer", "card_number");
        return new PgGovernedSqlService(connectionString, env).Executor();
    }

    private static DataSourceExecutionContext Context(TableFilterClause? filter, bool count = false)
    {
        var items = new Dictionary<string, object?>();
        if (filter != null) items[TableQueryItems.Filter] = filter;
        if (count) items[TableQueryItems.CountTotal] = true;
        return new DataSourceExecutionContext(
            SourceName: "sales",
            Metadata: OrderMetadata(),
            Principal: new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-FILTER-TEST")], "Test")),
            AccessDecision: TableAccessDecision.Allowed(Orders, Access),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "status", "amount"],
            Limit: 50,
            Offset: 0,
            Items: items);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_PushesDownFilter_AndFiltersRowsInDatabase()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        var filter = ODataFilterParser.Parse("status eq 'completed' and amount gt 300", DatabaseDialect.PostgreSql);
        var context = Context(filter, count: true);

        var rows = await CreateExecutor(db.ConnectionString).ExecuteAsync(context);

        rows.Count.ShouldBe(1);
        Convert.ToInt64(rows[0]["id"]).ShouldBe(3L);
        Convert.ToInt64(rows[0]["amount"]).ShouldBe(500L);
        context.Items[TableQueryItems.TotalCount].ShouldBe(1L);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_FilterWithContains_TranslatesToLike()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        var filter = ODataFilterParser.Parse("contains(status, 'com')", DatabaseDialect.PostgreSql);
        var context = Context(filter);

        var rows = await CreateExecutor(db.ConnectionString).ExecuteAsync(context);

        rows.Count.ShouldBe(2);
        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([2L, 3L]);
    }

    [RequiresContainerRuntimeFact]
    public async Task SqlExecutor_FilteringOnMaskedColumn_ZeroTrust_ThrowsSecurityException()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await SeedAsync(db.ConnectionString);
        var filter = ODataFilterParser.Parse("card_number eq '4111-1111'", DatabaseDialect.PostgreSql);
        var context = Context(filter);

        var ex = await Should.ThrowAsync<ArgumentException>(() => CreateExecutor(db.ConnectionString).ExecuteAsync(context));
        ex.Message.ShouldContain("Filtering on masked or denied column 'card_number'");
    }
}
