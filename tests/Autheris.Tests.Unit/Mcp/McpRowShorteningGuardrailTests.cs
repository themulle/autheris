namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class McpRowShorteningGuardrailTests
{
    private const string SqlArgs = """{"sql":"SELECT id, name FROM sales.public.orders ORDER BY id"}""";
    private const string DatasetArgs = """{"dataset":"sales.public.orders"}""";
    private const string OriginalNext = "original-next-token";

    private static JsonNode Row(int i, string? extra = null) =>
        new JsonObject { ["id"] = i, ["name"] = $"row-{i}-" + new string('q', 90), ["email"] = extra };

    private static string SqlPayload(int rows, Func<int, string?>? extra = null) => new JsonObject
    {
        ["columns"] = new JsonArray(
            new JsonObject { ["name"] = "id", ["type"] = "integer", ["nullable"] = false, ["masked"] = false },
            new JsonObject { ["name"] = "name", ["type"] = "varchar", ["nullable"] = true, ["masked"] = false }),
        ["rows"] = new JsonArray(Enumerable.Range(0, rows).Select(i => Row(i, extra?.Invoke(i))).ToArray()),
        ["rowCount"] = rows,
        ["truncated"] = false,
        ["page"] = new JsonObject { ["number"] = 1, ["offset"] = 0, ["size"] = rows },
        ["next"] = OriginalNext,
    }.ToJsonString();

    private static McpErrorTestRig Rig(string payload, int maxTokens = 250, Func<int, string?>? offer = null) => McpErrorTestRig.Create(
        execute: (_, _, _) =>
        {
            McpRowContinuation.Offer(offer);
            return payload;
        },
        tables: [McpErrorTestRig.Active("orders")],
        options: new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = maxTokens } });

    [Fact]
    public async Task QuerySql_OverBudget_SuccessWithWholeRows_ReturnedRowsSet()
    {
        var payload = SqlPayload(40);
        var rig = Rig(payload, offer: k => "resume-" + k);

        var result = await rig.Call("query_sql", SqlArgs);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ReturnedRows.ShouldNotBeNull();
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        root["rows"]!.AsArray().Count.ShouldBe(result.ReturnedRows!.Value);
        root["rowCount"]!.GetValue<int>().ShouldBe(result.ReturnedRows.Value);
        root["next"]!.GetValue<string>().ShouldBe("resume-" + result.ReturnedRows.Value);
        (result.ContentJson.Length / 4).ShouldBeLessThanOrEqualTo(250);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SingleRowOverBudget_IsTokenBudgetExceeded_NoPartialPayload()
    {
        var payload = new JsonObject { ["rows"] = new JsonArray(Row(1, new string('x', 2000))), ["rowCount"] = 1 }.ToJsonString();
        var rig = Rig(payload);

        var result = await rig.Call("sample_rows", DatasetArgs);

        result.IsSuccess.ShouldBeFalse();
        result.ReturnedRows.ShouldBeNull();
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        root.Select(p => p.Key).ShouldBe(["error"]);
        root["error"]!["code"]!.GetValue<string>().ShouldBe("TOKEN_BUDGET_EXCEEDED");
        root["error"]!["hint"]!.GetValue<string>().ShouldBe(McpRowShortening.FirstRowNote("sample_rows"));
        result.ContentJson.ShouldNotContain("xxxx");
        result.ContentJson.ShouldNotContain("row-1");
        var entry = rig.Audit.Entries.Single();
        entry.Decision.ShouldBe("ALLOW");
        JsonDocument.Parse(entry.DetailsJson!).RootElement.GetProperty("returnedRows").GetInt32().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shortening_AfterScrubbing_MaskedValuesStayMasked_IsMaskedTrue()
    {
        var payload = SqlPayload(40, i => $"person{i}@example.com");
        var rig = Rig(payload, offer: k => "resume-" + k);

        var result = await rig.Call("query_sql", SqlArgs);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
        result.ContentJson.ShouldNotContain("person");
        result.ContentJson.ShouldContain("***@example.com");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shortened_IsMasked_NeverLowerThanBeforeShortening()
    {
        // Only the last row (which the shortening drops) carries a value the scrubber masks: the flag stays true.
        var payload = SqlPayload(40, i => i == 39 ? "last@example.com" : null);
        var rig = Rig(payload);

        var result = await rig.Call("query_sql", SqlArgs);

        result.TruncatedDueToBudget.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shortened_OriginalNext_NeverInOutput()
    {
        foreach (var offer in new Func<int, string?>?[] { null, k => "resume-" + k })
        {
            var result = await Rig(SqlPayload(40), offer: offer).Call("query_sql", SqlArgs);

            result.IsSuccess.ShouldBeTrue();
            result.ContentJson.ShouldNotContain(OriginalNext);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Audit_Shortened_RecordsAllowTruncatedAndReturnedRows()
    {
        var rig = Rig(SqlPayload(40), offer: k => "resume-" + k);

        var result = await rig.Call("query_sql", SqlArgs);

        var entry = rig.Audit.Entries.Single();
        entry.Decision.ShouldBe("ALLOW");
        var details = JsonDocument.Parse(entry.DetailsJson!).RootElement;
        details.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        details.GetProperty("returnedRows").GetInt32().ShouldBe(result.ReturnedRows!.Value);
        details.GetProperty("estimatedTokens").GetInt32().ShouldBe(result.EstimatedTokens);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Shortened_AuditDetails_NoRowOrCursorText()
    {
        var rig = Rig(SqlPayload(40), offer: k => "resume-" + k);

        await rig.Call("query_sql", SqlArgs);

        var details = rig.Audit.Entries.Single().DetailsJson!;
        details.ShouldNotContain("row-");
        details.ShouldNotContain("resume-");
        details.ShouldNotContain(OriginalNext);
        details.ShouldNotContain("SELECT");
    }

    [Fact]
    public async Task UnderBudget_ContentByteIdentical_AuditHasNoReturnedRows()
    {
        var payload = SqlPayload(2);
        var rig = Rig(payload, maxTokens: 4096, offer: k => "resume-" + k);

        var result = await rig.Call("query_sql", SqlArgs);

        result.ContentJson.ShouldBe(payload);
        result.TruncatedDueToBudget.ShouldBeFalse();
        result.ReturnedRows.ShouldBeNull();
        rig.Audit.Entries.Single().DetailsJson.ShouldNotContain("returnedRows");
    }

    [Fact]
    public async Task QueryGraphQl_OverBudget_StaysTokenBudgetExceeded()
    {
        var payload = new JsonObject { ["data"] = new JsonObject { ["orders"] = new JsonArray(Enumerable.Range(0, 60).Select(i => Row(i)).ToArray()) } }.ToJsonString();
        var rig = McpErrorTestRig.Create(
            execute: (_, _, _) => payload,
            tables: [McpErrorTestRig.Active("orders")],
            options: new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = 250 } },
            graphQlCatalogMap: new OrdersGraphQlMap());

        var result = await rig.Call("query_graphql", """{"query":"{ orders { id } }"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ReturnedRows.ShouldBeNull();
        McpErrorEnvelopeAssert.Code(result.ContentJson).ShouldBe("TOKEN_BUDGET_EXCEEDED");
        result.ContentJson.ShouldNotContain("row-");
    }

    [Fact]
    public async Task QueryDataset_Shortened_ContinuationOffsetEqualsOffsetPlusCount()
    {
        var payload = new JsonObject
        {
            ["data"] = new JsonArray(Enumerable.Range(0, 40).Select(i => Row(i)).ToArray()),
            ["count"] = 40,
            ["offset"] = 20,
            ["limit"] = 40,
            ["hasMore"] = false,
        }.ToJsonString();
        var rig = Rig(payload);

        var result = await rig.Call("query_dataset", DatasetArgs);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        var root = JsonNode.Parse(result.ContentJson)!.AsObject();
        var count = root["count"]!.GetValue<int>();
        count.ShouldBe(result.ReturnedRows!.Value);
        root["hasMore"]!.GetValue<bool>().ShouldBeTrue();
        (root["offset"]!.GetValue<int>() + count).ShouldBe(20 + root["data"]!.AsArray().Count);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Offer_FromOtherToolName_NotUsedByGuardrail()
    {
        var payload = new JsonObject { ["rows"] = new JsonArray(Enumerable.Range(0, 40).Select(i => Row(i)).ToArray()), ["rowCount"] = 40 }.ToJsonString();
        var rig = Rig(payload, offer: k => "leaked-" + k);

        var result = await rig.Call("sample_rows", DatasetArgs);

        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ContentJson.ShouldNotContain("leaked-");
        JsonNode.Parse(result.ContentJson)!.AsObject().ContainsKey("next").ShouldBeFalse();
    }

    private sealed class OrdersGraphQlMap : Autheris.Application.Mcp.Interfaces.IGraphQlCatalogMap
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
