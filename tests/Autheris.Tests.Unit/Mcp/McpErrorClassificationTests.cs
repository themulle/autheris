namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>The guardrail classifies a tool result once: only a root-level error object is an error, and the token budget fails through an explicit flag.</summary>
[Trait("Category", "Security")]
public sealed class McpErrorClassificationTests
{
    private const string Args = """{"dataset":"sales.public.orders"}""";

    private static McpErrorTestRig Rig(string payload, int maxTokens = 0) => McpErrorTestRig.Create(
        execute: (_, _, _) => payload,
        tables: [McpErrorTestRig.Active("orders")],
        options: new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = maxTokens } });

    [Theory]
    [InlineData("""{"rows":[{"id":1,"error":"boom"}],"columns":["id","error"]}""")]
    [InlineData("""{"rows":[{"id":1,"error":{"code":"X","message":"m"}}]}""")]
    [InlineData("""{"data":{"error":{"code":"X"}},"rows":[]}""")]
    public async Task Success_WithColumnNamedError_IsNotAnError(string payload)
    {
        var result = await Rig(payload).Call("sample_rows", Args);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        result.ContentJson.ShouldBe(payload);
    }

    [Fact]
    public async Task Success_ResultWithNestedErrorSection_IsNotAnError()
    {
        // Same classification for a body whose nested members are called error: a dataset-shaped result with a nested error section.
        var result = await Rig("""{"results":[{"title":"t","error":{"note":"documented error"}}]}""").Call("sample_rows", Args);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GraphQlSuccessWithErrorsArray_IsNotToolErrorByErrorMember()
    {
        var result = await Rig("""{"data":{"x":1},"errors":[{"message":"partial"}]}""").Call("sample_rows", Args);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"error":{}}""")]
    [InlineData("""{"error":{"code":"WHATEVER_UPSTREAM","message":"upstream text for acme"}}""")]
    [InlineData("""{"error":{"code":-32000,"message":"rpc"}}""")]
    public async Task RootObjectError_IsError_FailClosed(string payload)
    {
        var result = await Rig(payload).Call("sample_rows", Args);

        result.IsSuccess.ShouldBeFalse();
        McpErrorEnvelopeAssert.Code(result).ShouldBe("EXECUTION_FAILED");
        result.ContentJson.ShouldNotContain("acme");
    }

    [Fact]
    public async Task RootObjectError_OwnEnvelope_KeepsItsCodeAndMessage()
    {
        var own = McpToolError.Serialize("QUERY_TIMEOUT", "The query timed out.");

        var result = await Rig(own).Call("sample_rows", Args);

        result.IsSuccess.ShouldBeFalse();
        result.ContentJson.ShouldBe(own);
    }

    [Fact]
    public async Task TokenBudgetReplacement_IsErrorViaFlag_NotBySniffing_AuditStaysAllowTruncated()
    {
        var payload = "{\"rows\":[" + string.Join(',', Enumerable.Range(0, 400).Select(i => $"{{\"id\":{i},\"name\":\"value-{i}\"}}")) + "]}";
        var rig = Rig(payload, maxTokens: 50);

        var result = await rig.Call("sample_rows", Args);

        result.IsSuccess.ShouldBeFalse();
        result.TruncatedDueToBudget.ShouldBeTrue();
        var error = McpErrorEnvelopeAssert.Error(result.ContentJson);
        error.GetProperty("code").GetString().ShouldBe("TOKEN_BUDGET_EXCEEDED");
        error.GetProperty("hint").GetString().ShouldNotBeNullOrWhiteSpace();
        result.ContentJson.ShouldNotContain("value-");
        var entry = rig.Audit.Entries.Single();
        entry.Decision.ShouldBe("ALLOW");
        entry.DetailsJson.ShouldContain("\"truncated\":true");
    }

    [Fact]
    public async Task SuccessResult_KeepsMetaIsMaskedAndEstimatedTokens()
    {
        var result = await Rig("""{"rows":[{"email":"someone@example.com"}]}""").Call("sample_rows", Args);

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
        result.EstimatedTokens.ShouldBeGreaterThan(0);
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task ErrorResult_HasNoMeta_NoPartialPayload()
    {
        var result = await Rig("""{"error":{"code":"QUERY_TIMEOUT","message":"m"},"rows":[{"id":1,"secret":"s"}]}""").Call("sample_rows", Args);

        result.IsSuccess.ShouldBeFalse();
        result.ContentJson.ShouldNotContain("secret");
        result.ContentJson.ShouldNotContain("_meta");
        result.EstimatedTokens.ShouldBe(0);
    }
}
