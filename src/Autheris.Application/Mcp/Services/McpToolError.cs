namespace Autheris.Application.Mcp.Services;

using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Common;

/// <summary>
/// The single producer of MCP tool error results (NL-SC-31): the executor shape <c>{"isError":true,"error":{"code","message"}}</c>
/// with a fixed code and message. Nothing else (no input echo, no exception text) is written; the AI guardrail treats it as an error result.
/// </summary>
public static partial class McpToolError
{
    public const int MaxMessageLength = 256;
    public const string FallbackCode = "ERROR";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    private static partial Regex CodePattern();

    /// <summary>Serializes the executor error shape; <paramref name="tenantId"/> and <paramref name="tool"/> (when given) are the caller's own routing context, never request input.</summary>
    public static string Serialize(string code, string message, string? tenantId = null, string? tool = null)
    {
        var safeCode = !string.IsNullOrEmpty(code) && CodePattern().IsMatch(code) ? code : FallbackCode;
        var safeMessage = ErrorText.Sanitize(message, "The request failed.", MaxMessageLength);
        var error = new { code = safeCode, message = safeMessage };
        return tenantId == null && tool == null
            ? JsonSerializer.Serialize(new { isError = true, error }, JsonOptions)
            : JsonSerializer.Serialize(new { tenantId, tool, isError = true, error }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
