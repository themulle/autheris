namespace Autheris.Tests.Unit.Mcp;

using System.Text.Json;
using Shouldly;

/// <summary>Assertions on the one MCP tool error envelope <c>{"error":{"code","message",...}}</c>.</summary>
internal static class McpErrorEnvelopeAssert
{
    /// <summary>Asserts the envelope shape (only a root <c>error</c>, string code and message, no routing members) and returns the error element.</summary>
    public static JsonElement Error(string? json)
    {
        json.ShouldNotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(["error"]);
        var error = root.GetProperty("error");
        error.ValueKind.ShouldBe(JsonValueKind.Object);
        error.GetProperty("code").ValueKind.ShouldBe(JsonValueKind.String);
        error.GetProperty("message").ValueKind.ShouldBe(JsonValueKind.String);
        json.ShouldNotContain("tenantId");
        json.ShouldNotContain("isError");
        return error.Clone();
    }

    public static string Code(string? json) => Error(json).GetProperty("code").GetString()!;

    /// <summary>The <c>detail</c> member (a fixed argument text of the server), or null.</summary>
    public static string? Detail(string? json) =>
        Error(json).TryGetProperty("detail", out var d) ? d.GetString() : null;

    public static string Message(string? json) => Error(json).GetProperty("message").GetString()!;

    /// <summary>The code of a failed tool result (the envelope is both the content and the error message).</summary>
    public static string Code(Autheris.Domain.Model.McpToolCallResult result)
    {
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(result.ContentJson);
        return Code(result.ErrorMessage);
    }
}
