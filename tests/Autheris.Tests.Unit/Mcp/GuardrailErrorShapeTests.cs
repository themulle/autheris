namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>NL-SC-04 / NL-SC-06 (O-5a, O-5c): the AI guardrail error detector stays fail-closed across the bare-shape rename and recognises governed ProblemDetails.</summary>
[Trait("Category", "Security")]
public sealed class GuardrailErrorShapeTests
{
    [Theory]
    [InlineData("""{"error":"InvalidParams"}""", "InvalidParams")]
    [InlineData("""{"error":"InvalidParams","message":"m"}""", "InvalidParams")]
    [InlineData("""{"error":"free text with blanks"}""", "ERROR")]
    public void Guardrail_Bare_Error_Shape_Still_Audited_Deny_After_Rename(string json, string code)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out var errorCode).ShouldBeTrue();
        errorCode.ShouldBe(code);
    }

    [Theory]
    [InlineData("""{"error":"x","rows":[]}""")]
    [InlineData("""{"error":5}""")]
    [InlineData("""{"error":""}""")]
    [InlineData("""{"error":"  "}""")]
    [InlineData("""{"message":"m"}""")]
    public void Guardrail_Bare_Error_Shape_Rejects_Data_Rows_And_Malformed_Errors(string json)
    {
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
}
