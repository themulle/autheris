namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

public sealed partial class McpQuerySqlPagingTests
{
    private const string ViewSql = "SELECT id, name FROM sales.public.orders WHERE id >= @min";

    private static string ViewArgs(string view) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["query"] = ViewSql,
            ["parameters"] = new { min = 1 },
            ["view"] = JsonDocument.Parse(view).RootElement.Clone()
        });

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_View_IsComposedAndItsValuesAreBoundNeverInterpolated()
    {
        var bound = new List<IReadOnlyList<object?>>();
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5, boundValues: bound);
        const string value = "x' OR '1'='1";

        var json = await handler.ExecuteToolAsync(
            QuerySqlTool,
            ViewArgs($$"""{ "filter": { "column": "name", "op": "eq", "value": "{{value}}" }, "orderBy": [ { "column": "id", "direction": "desc" } ] }"""),
            CreateSession());

        ErrorCode(json).ShouldBeNull();
        executedSql.ShouldNotBeEmpty();
        executedSql.ShouldAllBe(sql => !sql.Contains(value, StringComparison.Ordinal) && !sql.Contains("'1'='1", StringComparison.Ordinal));
        executedSql.ShouldContain(sql => sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
        Flatten(bound).ShouldContain(value);
    }

    [Theory]
    [InlineData("""{ "filter": { "column": "email", "op": "eq", "value": "x" } }""")]
    [InlineData("""{ "groupBy": ["name"] }""")]
    [InlineData("""{ "orderBy": [ { "column": "nope" } ] }""")]
    [Trait("Category", "Security")]
    public async Task QuerySql_RefusedView_IsInvalidParamsAndNothingRuns(string view)
    {
        var (handler, _, executedSql) = CreatePipeline(maxResultRows: 5);

        var json = await handler.ExecuteToolAsync(QuerySqlTool, ViewArgs(view), CreateSession());

        ErrorCode(json).ShouldNotBeNull();
        executedSql.ShouldBeEmpty();
    }
}
