namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>query_graphql answers the body of <c>POST /graphql</c> for the same query as the same caller.</summary>
public sealed partial class McpHttpContractEndToEndTests
{
    private async Task<string> GraphQlFieldAsync(HttpClient client)
    {
        await SeedAsync();
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (isError, text) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "default.main.mcp_http_rows" });
        isError.ShouldBeFalse(text);
        var field = JsonNode.Parse(text)!["graphQl"]!["queryField"]!.GetValue<string>();
        await TestSupport.GraphQlSchemaReadiness.AwaitCatalogSchemaAsync(_factory.Services, [field], TestSupport.GraphQlSchemaReadiness.DefaultTimeout);
        return field;
    }

    private static async Task<(bool IsError, JsonNode Body, JsonNode? Meta, string Text)> McpGraphQlAsync(HttpClient client, string query, object? variables = null)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        object arguments = variables is null ? new { query } : new { query, variables };
        var result = await McpTestClient.CallResultAsync(mcp, "query_graphql", arguments);
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, JsonNode.Parse(text)!, result.Meta is null ? null : JsonNode.Parse(result.Meta.ToJsonString()), text);
    }

    private static async Task<(int Status, JsonNode Body, string Text)> HttpGraphQlAsync(HttpClient client, string query, object? variables = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", variables is null ? new { query } : (object)new { query, variables });
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, JsonNode.Parse(text)!, text);
    }

    private static string FirstErrorCode(JsonNode body) => body["errors"]![0]!["extensions"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task QueryGraphQl_First500_DataAndExtensionsEqualGraphQl()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);
        var query = $"{{ {field}(first: 500) {{ id label }} }}";

        var mcp = await McpGraphQlAsync(client, query);
        var http = await HttpGraphQlAsync(client, query);

        mcp.IsError.ShouldBeFalse(mcp.Text);
        http.Status.ShouldBe(200, http.Text);
        mcp.Body["data"]![field]!.AsArray().Count.ShouldBe(500);
        JsonNode.DeepEquals(mcp.Body["data"], http.Body["data"]).ShouldBeTrue();
        JsonNode.DeepEquals(mcp.Body["extensions"], http.Body["extensions"]).ShouldBeTrue($"MCP {mcp.Text} vs HTTP {http.Text}");
        mcp.Body.AsObject().ContainsKey("truncated").ShouldBeFalse();
        mcp.Body.AsObject().ContainsKey("rowCap").ShouldBeFalse();
    }

    [Fact]
    public async Task QueryGraphQl_VariableFirst_CostExtensionEqualsGraphQl()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);
        var query = $"query Q($n: Int) {{ {field}(first: $n) {{ id }} }}";

        var mcp = await McpGraphQlAsync(client, query, new { n = 50 });
        var http = await HttpGraphQlAsync(client, query, new { n = 50 });

        mcp.IsError.ShouldBeFalse(mcp.Text);
        JsonNode.DeepEquals(mcp.Body["data"], http.Body["data"]).ShouldBeTrue();
        JsonNode.DeepEquals(mcp.Body["extensions"], http.Body["extensions"]).ShouldBeTrue($"MCP {mcp.Text} vs HTTP {http.Text}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_ErrorsEqualGraphQl_AfterSanitizerAndShield()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);

        foreach (var query in new[] { $"{{ {field}(first: -1) {{ id }} }}", $"{{ {field}(first: 5) {{ id no_such_column }} }}" })
        {
            var mcp = await McpGraphQlAsync(client, query);
            var http = await HttpGraphQlAsync(client, query);

            mcp.IsError.ShouldBeTrue(mcp.Text);
            mcp.Body["error"]!["code"]!.GetValue<string>().ShouldBe(FirstErrorCode(http.Body));
            JsonNode.DeepEquals(mcp.Body["errors"], http.Body["errors"]).ShouldBeTrue($"MCP {mcp.Text} vs HTTP {http.Text}");
            mcp.Body.AsObject().Select(p => p.Key).ShouldBe(["error", "errors"]);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_UnknownVsForbiddenNestedField_ErrorsByteIdentical()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);

        // Both names have the same length, so every position in the error is the same.
        var unknown = await McpGraphQlAsync(client, $"{{ {field}(first: 5) {{ id unknown_x }} }}");
        var forbidden = await McpGraphQlAsync(client, $"{{ {field}(first: 5) {{ id denied_x }} }}");

        unknown.IsError.ShouldBeTrue(unknown.Text);
        forbidden.Text.ShouldBe(unknown.Text);
        unknown.Text.ShouldNotContain("denied_x");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_FirstAboveMaxResponseRows_RejectedLikeGraphQl()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);
        var query = $"{{ {field}(first: 99999999) {{ id }} }}";

        var mcp = await McpGraphQlAsync(client, query);
        var http = await HttpGraphQlAsync(client, query);

        mcp.IsError.ShouldBeTrue(mcp.Text);
        mcp.Body["error"]!["code"]!.GetValue<string>().ShouldBe(FirstErrorCode(http.Body));
        JsonNode.DeepEquals(mcp.Body["errors"], http.Body["errors"]).ShouldBeTrue($"MCP {mcp.Text} vs HTTP {http.Text}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryGraphQl_VariableFirstOverCost_SameCodeAsGraphQl()
    {
        var client = Client();
        var field = await GraphQlFieldAsync(client);
        var query = $"query Q($n: Int) {{ {field}(first: $n) {{ id }} }}";

        var mcp = await McpGraphQlAsync(client, query, new { n = 99999999 });
        var http = await HttpGraphQlAsync(client, query, new { n = 99999999 });

        mcp.IsError.ShouldBeTrue(mcp.Text);
        mcp.Body["error"]!["code"]!.GetValue<string>().ShouldBe(FirstErrorCode(http.Body));
    }
}
