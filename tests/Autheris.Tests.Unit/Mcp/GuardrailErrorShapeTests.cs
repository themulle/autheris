namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>NL-SC-04 / NL-SC-06 / NL-SC-25 (O-5a, O-5c): the AI guardrail error detector recognises the executor shape, governed ProblemDetails and relayed protocol errors; the legacy bare shape is gone.</summary>
[Trait("Category", "Security")]
public sealed class GuardrailErrorShapeTests
{
    [Theory]
    [InlineData("""{"error":"x","rows":[]}""")]
    [InlineData("""{"error":5}""")]
    [InlineData("""{"error":""}""")]
    [InlineData("""{"error":"free text"}""")]
    [InlineData("""{"error":"x","status":200}""")]
    [InlineData("""{"message":"m"}""")]
    [InlineData("""{"rows":[{"error":"cell"}]}""")]
    public void Guardrail_Does_Not_Mistake_Data_Or_Legacy_Bare_Shapes_For_Errors(string json)
    {
        // NL-SC-25: the legacy bare shape is no longer produced (ratchet baseline empty) and is no longer special-cased.
        AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"type":"urn:autheris:error:FORBIDDEN","title":"Forbidden","status":403,"detail":"d","code":"FORBIDDEN","correlationId":"c","traceId":"t"}""", "FORBIDDEN")]
    [InlineData("""{"status":500,"code":"INTERNAL"}""", "INTERNAL")]
    public void Guardrail_Denies_ProblemDetails_Tool_Result_After_Error_Member_Removal(string json, string code)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out var errorCode).ShouldBeTrue();
        errorCode.ShouldBe(code);
    }

    [Theory]
    [InlineData("""{"status":200,"code":"OK"}""")]
    [InlineData("""{"status":"403","code":"X"}""")]
    [InlineData("""{"status":403}""")]
    [InlineData("""{"status":403,"code":7}""")]
    [InlineData("""{"status":403,"code":""}""")]
    public void Guardrail_ProblemDetails_Detection_Requires_Error_Status_And_String_Code(string json)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeFalse();
    }

    [Fact]
    public void Guardrail_Denies_ProblemDetails_Tool_Result()
    {
        // NL-T-24 / NL-SC-26: a problem body that only carries a type URI is an error result too.
        AiDataGuardrailService.TryGetExecutorError("""{"type":"urn:autheris:error:ACCESS_DENIED","status":403}""", out var code).ShouldBeTrue();
        code.ShouldBe("ERROR");
    }

    [Fact]
    public void Guardrail_Denies_All_Mcp_Reachable_Error_Shapes()
    {
        // NL-T-24: every shape an MCP-reachable tool can return as an error is audited DENY.
        var shapes = new[]
        {
            McpToolError.Serialize("INVALID_PARAMS", "m"),
            McpToolError.Serialize("FORBIDDEN", "m", "tenant", "tool"),
            """{"isError":true,"error":{"code":"X","message":"m"}}""",
            // Relayed protocol shapes (NL-SC-29): OData, Iceberg, Backstage, JSON-RPC and the Envoy pass-through.
            """{"error":{"code":"NotFound","message":"m"}}""",
            """{"error":{"message":"m","type":"NoSuchTableException","code":404}}""",
            """{"error":{"name":"NotFoundError","message":"m"},"response":{"statusCode":404}}""",
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"m"}}""",
            """{"error":"Access Denied by Autheris PDP","status":403}""",
            """{"type":"urn:autheris:error:BAD_REQUEST","title":"Bad Request","status":400,"detail":"d","code":"BAD_REQUEST"}"""
        };
        foreach (var json in shapes)
        {
            AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeTrue(json);
        }
    }
}
