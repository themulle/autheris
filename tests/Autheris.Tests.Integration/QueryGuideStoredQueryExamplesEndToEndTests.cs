namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// The stored-query examples printed in the agent query guide run unchanged (only the table field name is substituted) against a real
/// catalog, with and without each optional variable.
/// </summary>
public sealed class QueryGuideStoredQueryExamplesEndToEndTests : IClassFixture<QueryGuideStoredQueryExamplesEndToEndTests.ExamplesFixture>
{
    private const string Field = "default_main_orders";
    private static readonly TableIdentifier OrdersId = new("default", "main", "orders");

    private readonly ExamplesFixture _fixture;

    public QueryGuideStoredQueryExamplesEndToEndTests(ExamplesFixture fixture) => _fixture = fixture;

    public sealed class ExamplesFixture : RowFilterChannelParityTests.Fixture
    {
        protected override IReadOnlyCollection<string> RequiredGraphQlFields =>
            [.. base.RequiredGraphQlFields, Field, Field + "_aggregate"];

        // The guide examples page with a default of 50 rows; lift the per-query cost budget so that default fits (budget only, no auth or masking change).
        protected override void ConfigureHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "10000");
            builder.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxCostPerQuery", "10000");
            // The connection example nests edges/node/aggregates deeper than the Standard tier's default depth of 10.
            builder.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxComplexityDepth", "30");
        }

        protected override void SeedExtraData(NpgsqlConnection connection)
        {
            using var cmd = connection.CreateCommand();
            var rows = string.Join(",", Enumerable.Range(1, 30).Select(i =>
                $"({i},'{(i % 3 == 0 ? "PAID" : i % 3 == 1 ? "PENDING" : "APPROVED")}',{i * 10})"));
            cmd.CommandText = $"CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT NOT NULL, amount INTEGER NOT NULL); INSERT INTO orders VALUES {rows};";
            cmd.ExecuteNonQuery();
        }

        protected override async Task SeedExtraGovernanceAsync(IGovernanceRepository repo)
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = "orders", TableName = "orders", SchemaName = "main", SourceType = "PostgreSql", SourceName = "default" },
                Identifier = OrdersId,
                PrimaryKeyColumns = ["id"],
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "status", DataType = "varchar" },
                    new TableColumn { ColumnName = "amount", DataType = "integer" }
                ]
            });
            var orders = (await repo.GetTableMetadataAsync(OrdersId))!;
            await repo.CreateConsentAsync(new Consent
            {
                TableId = orders.Table.Id,
                TableIdentifier = OrdersId,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Group,
                GranteeSid = new Sid(ReadersGroupSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = orders.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
                RowFilters = []
            });
        }
    }

    private async Task<JsonDocument> RunAsync(string example, string variablesJson)
    {
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var query = Regex.Replace(example, @"\borders", Field);
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables = JsonNode.Parse(variablesJson) });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static bool HasErrors(JsonDocument doc) => doc.RootElement.TryGetProperty("errors", out _);

    private static long[] ListIds(JsonDocument doc) =>
        doc.RootElement.GetProperty("data").GetProperty(Field).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray();

    private static JsonElement Connection(JsonDocument doc) => doc.RootElement.GetProperty("data").GetProperty(Field + "Connection");

    private static long[] ConnectionIds(JsonDocument doc) =>
        Connection(doc).GetProperty("edges").EnumerateArray().Select(e => e.GetProperty("node").GetProperty("id").GetInt64()).ToArray();

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"where":null}""")]
    [InlineData("""{"where":{}}""")]
    public async Task ListExample_WithoutFilter_ReturnsAllRowsInKeyOrder(string variables)
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ListExample, variables);

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        ListIds(doc).ShouldBe(Enumerable.Range(1, 30).Select(i => (long)i).ToArray());
    }

    [Fact]
    public async Task ListExample_WithFilterOrderAndOffset_ReturnsTheRequestedPage()
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ListExample,
            """{"where":{"amount":{"gte":100}},"orderBy":[{"amount":"DESC"}],"first":5,"offset":5}""");

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        ListIds(doc).ShouldBe([25L, 24, 23, 22, 21]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ListExample_NullComparisonOperand_MatchesNoRow()
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ListExample, """{"where":{"status":{"eq":null}}}""");

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        ListIds(doc).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ListExample_NullListOperand_IsRejected()
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ListExample, """{"where":{"status":{"in":null}}}""");

        HasErrors(doc).ShouldBeTrue(doc.RootElement.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"filter":{}}""")]
    public async Task ConnectionExample_WithoutFilter_ReturnsPaidRowsInKeyOrder(string variables)
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ConnectionExample, variables);

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        ConnectionIds(doc).ShouldBe(Enumerable.Range(1, 10).Select(i => (long)i * 3).ToArray());
        Connection(doc).GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ConnectionExample_PagesWithFilterDirectionAndCursor_ReturnsEveryRowOnce()
    {
        var seen = new List<long>();
        string? after = null;
        var hasNext = true;
        for (var page = 0; hasNext && page < 10; page++)
        {
            var variables = new JsonObject
            {
                ["filter"] = JsonNode.Parse("""{"amount":{"gte":100}}"""),
                ["dir"] = "DESC",
                ["first"] = 3
            };
            if (after is not null)
            {
                variables["after"] = after;
            }

            using var doc = await RunAsync(McpQueryGuideBuilder.ConnectionExample, variables.ToJsonString());
            HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
            seen.AddRange(ConnectionIds(doc));
            var pageInfo = Connection(doc).GetProperty("pageInfo");
            hasNext = pageInfo.GetProperty("hasNextPage").GetBoolean();
            after = pageInfo.GetProperty("endCursor").GetString();
        }

        hasNext.ShouldBeFalse();
        seen.ShouldBe([30L, 27, 24, 21, 18, 15, 12]);
    }

    /// <summary>A null for the non-null <c>$filter</c> variable is rejected at variable coercion, so the response carries no data.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task ConnectionExample_NullFilterMember_IsRejected()
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.ConnectionExample, """{"filter":null}""");

        HasErrors(doc).ShouldBeTrue(doc.RootElement.ToString());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConnectionExample_CursorWithChangedFilter_IsRejected()
    {
        string cursor;
        using (var first = await RunAsync(McpQueryGuideBuilder.ConnectionExample, """{"filter":{},"first":3}"""))
        {
            HasErrors(first).ShouldBeFalse(first.RootElement.ToString());
            cursor = Connection(first).GetProperty("pageInfo").GetProperty("endCursor").GetString()!;
        }

        var variables = new JsonObject
        {
            ["filter"] = JsonNode.Parse("""{"amount":{"gte":100}}"""),
            ["first"] = 3,
            ["after"] = cursor
        };
        using var doc = await RunAsync(McpQueryGuideBuilder.ConnectionExample, variables.ToJsonString());

        HasErrors(doc).ShouldBeTrue(doc.RootElement.ToString());
    }

    [Fact]
    public async Task Connection_OrderByOnNonKeyColumn_IsRejected()
    {
        using var doc = await RunAsync($"{{ {Field}Connection(orderBy: [{{ amount: ASC }}]) {{ edges {{ cursor }} }} }}", "{}");

        doc.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
    }

    [Theory]
    [InlineData("""{"aggregates":[{"fn":"COUNT","alias":"n"}]}""")]
    [InlineData("""{"groupBy":null,"aggregates":[{"fn":"COUNT","alias":"n"}],"where":null,"orderBy":null,"limit":null}""")]
    public async Task AggregateExample_WithoutOptionalArguments_ReturnsOneTotal(string variables)
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.AggregateExample, variables);

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        var groups = doc.RootElement.GetProperty("data").GetProperty(Field + "_aggregate").GetProperty("groups");
        groups.GetArrayLength().ShouldBe(1);
        groups[0].GetProperty("aggregates")[0].GetProperty("intValue").GetInt64().ShouldBe(30);
    }

    [Fact]
    public async Task AggregateExample_WithEveryArgument_ReturnsOneGroupPerStatus()
    {
        using var doc = await RunAsync(McpQueryGuideBuilder.AggregateExample,
            """{"groupBy":["status"],"aggregates":[{"fn":"COUNT","alias":"n"}],"where":{"amount":{"gte":100}},"orderBy":[{"alias":"n","direction":"DESC"}],"limit":10}""");

        HasErrors(doc).ShouldBeFalse(doc.RootElement.ToString());
        var data = doc.RootElement.GetProperty("data").GetProperty(Field + "_aggregate");
        data.GetProperty("suppressed").GetBoolean().ShouldBeFalse();
        var groups = data.GetProperty("groups").EnumerateArray().ToArray();
        groups.Length.ShouldBe(3);
        groups.ShouldAllBe(g => g.GetProperty("rowCount").GetInt64() == 7);
    }
}
