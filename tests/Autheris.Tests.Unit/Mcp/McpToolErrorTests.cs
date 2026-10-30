namespace Autheris.Tests.Unit.Mcp;

using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>NL-SC-31: MCP tool errors come from one helper with a fixed code and message; the guardrail treats them as an error result.</summary>
[Trait("Category", "Security")]
public sealed class McpToolErrorTests
{
    [Fact]
    public void Serialize_Writes_Only_The_Executor_Shape()
    {
        var json = McpToolError.Serialize("INVALID_PARAMS", "Argument 'metric' is required.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["error", "isError"]);
        root.GetProperty("isError").GetBoolean().ShouldBeTrue();
        root.GetProperty("error").EnumerateObject().Select(p => p.Name).Order().ShouldBe(["code", "message"]);
        root.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("INVALID_PARAMS");
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("HAS SPACE")]
    [InlineData("")]
    [InlineData("X")]
    public void Ungoverned_Code_Falls_Back_To_ERROR(string code)
    {
        using var doc = JsonDocument.Parse(McpToolError.Serialize(code, "m"));
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("ERROR");
    }

    [Fact]
    public void Message_Is_Sanitized_And_Capped()
    {
        using var doc = JsonDocument.Parse(McpToolError.Serialize("NOT_FOUND", "a\r\nb" + new string('z', 5000)));
        var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        message.ShouldNotContain("\n");
        message.Length.ShouldBeLessThanOrEqualTo(McpToolError.MaxMessageLength);
    }
}
