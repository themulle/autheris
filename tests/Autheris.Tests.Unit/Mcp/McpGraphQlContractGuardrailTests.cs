namespace Autheris.Tests.Unit.Mcp;

using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>The guardrail runs the same guards on a query_graphql body as on every data result: errors are scrubbed, partial data is a result.</summary>
[Trait("Category", "Security")]
public sealed class McpGraphQlContractGuardrailTests
{
    private const string CostError = """{"message":"Too costly for bob@corp.example.","path":["orders"],"extensions":{"code":"GRAPHQL_COST_EXCEEDED"}}""";

    private static McpErrorTestRig Rig(string payload, int maxTokens = 128_000) => McpErrorTestRig.Create(
        execute: (_, _, _) => payload,
        tables: [McpErrorTestRig.Active("orders")],
        options: new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = maxTokens } },
        graphQlCatalogMap: new OrdersMap());

    private static Task<Autheris.Domain.Model.McpToolCallResult> Call(McpErrorTestRig rig) =>
        rig.Call("query_graphql", """{"query":"{ orders { id } }"}""").AsTask();

    [Fact]
    public async Task QueryGraphQl_ErrorBranch_KeepsErrorsVerbatim_ViaGuardrail()
    {
        var errors = """[{"message":"Too costly.","path":["orders"],"extensions":{"code":"GRAPHQL_COST_EXCEEDED","cost":900}}]""";
        var rig = Rig(McpToolError.SerializeGraphQl("{\"data\":null,\"errors\":" + errors + "}"));

        var result = await Call(rig);

        result.IsSuccess.ShouldBeFalse();
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        root.Select(p => p.Key).ShouldBe(["error", "errors"]);
        root["error"]!["code"]!.GetValue<string>().ShouldBe("GRAPHQL_COST_EXCEEDED");
        JsonNode.DeepEquals(root["errors"], JsonNode.Parse(errors)).ShouldBeTrue();
    }

    [Fact]
    public async Task QueryGraphQl_ErrorBranch_ScrubsPiiInErrors()
    {
        var rig = Rig(McpToolError.SerializeGraphQl("{\"data\":null,\"errors\":[" + CostError + "]}"));

        var result = await Call(rig);

        result.IsSuccess.ShouldBeFalse();
        result.ContentJson.ShouldNotContain("bob@corp.example");
        result.IsMasked.ShouldBeTrue();
        JsonNode.Parse(result.ContentJson)!["errors"]!.AsArray().Count.ShouldBe(1);
        JsonNode.Parse(result.ContentJson)!["error"]!["code"]!.GetValue<string>().ShouldBe("GRAPHQL_COST_EXCEEDED");
    }

    [Fact]
    public async Task QueryGraphQl_PartialData_RunsScrubInspectBudgetAndMeta()
    {
        var rig = Rig("{\"data\":{\"orders\":[{\"id\":1,\"email\":\"bob@corp.example\"}],\"other\":null},\"errors\":[" + CostError + "]}");

        var result = await Call(rig);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        result.ContentJson.ShouldNotContain("bob@corp.example");
        result.IsMasked.ShouldBeTrue();
        result.EstimatedTokens.ShouldBeGreaterThan(0);
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        root.ContainsKey("error").ShouldBeFalse();
        root["errors"]!.AsArray().Count.ShouldBe(1);
        root["data"]!["orders"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task QueryGraphQl_PartialData_OverBudget_TokenBudgetExceeded_NoShortening()
    {
        var rows = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode)new JsonObject { ["id"] = i, ["name"] = "row-" + i + new string('q', 90) }).ToArray());
        var payload = new JsonObject
        {
            ["data"] = new JsonObject { ["orders"] = rows },
            ["errors"] = JsonNode.Parse("[" + CostError.Replace("bob@corp.example", "x") + "]")
        }.ToJsonString();
        var rig = Rig(payload, maxTokens: 250);

        var result = await Call(rig);

        result.IsSuccess.ShouldBeFalse();
        result.ReturnedRows.ShouldBeNull();
        McpErrorEnvelopeAssert.Code(result.ContentJson).ShouldBe("TOKEN_BUDGET_EXCEEDED");
        result.ContentJson.ShouldNotContain("row-");
    }

    [Fact]
    public async Task QueryGraphQl_NullDataWithErrors_IsErrorEnvelope()
    {
        var rig = Rig(McpToolError.SerializeGraphQl("{\"data\":null,\"errors\":[" + CostError.Replace("bob@corp.example", "x") + "]}"));

        var result = await Call(rig);

        result.IsSuccess.ShouldBeFalse();
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        root.ContainsKey("data").ShouldBeFalse();
        root.ContainsKey("error").ShouldBeTrue();
    }

    private sealed class OrdersMap : Autheris.Application.Mcp.Interfaces.IGraphQlCatalogMap
    {
        public Task<Autheris.Domain.Model.GraphQlTableMapping?> GetTableAsync(Autheris.Domain.Common.TableIdentifier table, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<Autheris.Domain.Model.GraphQlTableMapping?>(null);

        public Task<string?> GetRootFieldAsync(Autheris.Domain.Common.TableIdentifier table, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<Autheris.Domain.Common.TableIdentifier?> ResolveRootFieldAsync(string field, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<Autheris.Domain.Common.TableIdentifier?>(null);

        public Task<System.Collections.Generic.IReadOnlyList<Autheris.Domain.Common.TableIdentifier>?> ResolveDocumentTablesAsync(string document, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<Autheris.Domain.Common.TableIdentifier>?>([McpErrorTestRig.Table("orders")]);
    }
}
