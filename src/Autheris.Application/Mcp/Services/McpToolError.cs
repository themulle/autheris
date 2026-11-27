namespace Autheris.Application.Mcp.Services;

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Common;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;

/// <summary>The optional members of the error envelope (same names and meaning as the WebSQL problem extensions).</summary>
/// <param name="Code">Stable upper-case code.</param>
/// <param name="Message">Fixed message of the code.</param>
public sealed record McpToolErrorBody(string Code, string Message)
{
    public string? Hint { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Limit { get; init; }
    public int? EstimatedCost { get; init; }
    public int? MaxAllowedCost { get; init; }
    public int? RetryAfterSeconds { get; init; }

    /// <summary>The GraphQL execution result of a GraphQL tool failure; written unchanged under <c>error.graphQL</c>.</summary>
    public JsonElement? GraphQL { get; init; }
}

/// <summary>
/// The single producer of MCP tool error results: <c>{"error":{"code","message"[,"hint","line","column","limit","estimatedCost",
/// "maxAllowedCost","retryAfterSeconds","graphQL"]}}</c>. The root carries nothing else (no tenant, tool or flag). The message is the
/// fixed message of the code; nothing of the request or of an exception is written.
/// </summary>
public static partial class McpToolError
{
    public const int MaxMessageLength = 256;
    public const int MaxHintLength = 512;
    public const int MaxNormalizeInputLength = 256 * 1024;
    public const string FallbackCode = "ERROR";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    private static partial Regex CodePattern();

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Strings written by this class (by reference): only those carry a message that may be passed on unchanged.
    private static readonly ConditionalWeakTable<string, object> Produced = new();

    private static readonly object Marker = new();

    /// <summary>Serializes an envelope of a code and a message (both guarded).</summary>
    public static string Serialize(string code, string message) => Serialize(new McpToolErrorBody(code, message));

    /// <summary>Serializes the envelope in its fixed member order; null members are omitted.</summary>
    public static string Serialize(McpToolErrorBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var code = !string.IsNullOrEmpty(body.Code) && CodePattern().IsMatch(body.Code) ? body.Code : FallbackCode;
        var message = ErrorText.Sanitize(body.Message, "The request failed.", MaxMessageLength);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            if (body.Hint is { } hint && ErrorText.Sanitize(hint, string.Empty, MaxHintLength) is { Length: > 0 } safeHint)
            {
                w.WriteString("hint", safeHint);
            }

            if (body.Line is { } line) w.WriteNumber("line", line);
            if (body.Column is { } column) w.WriteNumber("column", column);
            if (body.Limit is { } limit && ErrorText.Sanitize(limit, string.Empty, 64) is { Length: > 0 } safeLimit) w.WriteString("limit", safeLimit);
            if (body.EstimatedCost is { } est) w.WriteNumber("estimatedCost", est);
            if (body.MaxAllowedCost is { } max) w.WriteNumber("maxAllowedCost", max);
            if (body.RetryAfterSeconds is { } retry) w.WriteNumber("retryAfterSeconds", retry);
            if (body.GraphQL is { ValueKind: JsonValueKind.Object } graphQl)
            {
                w.WritePropertyName("graphQL");
                graphQl.WriteTo(w);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        Produced.TryAdd(json, Marker);
        return json;
    }

    /// <summary>A failed tool result whose message is the fixed message of the code (or the sanitised <paramref name="text"/>).</summary>
    public static McpToolCallResult Fail(string code, string? text = null)
    {
        var envelope = Serialize(code, ErrorText.Sanitize(text, McpErrorCodes.FixedMessage(code), MaxMessageLength));
        return new McpToolCallResult(IsSuccess: false, ContentJson: envelope, ErrorMessage: envelope);
    }

    /// <summary>A failed tool result for an envelope that was already built.</summary>
    public static McpToolCallResult FailWith(string envelopeJson) =>
        new(IsSuccess: false, ContentJson: envelopeJson, ErrorMessage: envelopeJson);

    /// <summary>The envelope body of a governed SQL error: the stable code, the fixed message and the position/limit/retry fields of the WebSQL problem.</summary>
    public static McpToolErrorBody FromSql(Exception? exception, in SqlErrorMapping mapped)
    {
        int? estimated = null, maxAllowed = null, retry = null;
        switch (exception)
        {
            case SqlCostExceededException cost:
                estimated = cost.EstimatedCost;
                maxAllowed = cost.MaxAllowedCost;
                break;
            case SqlQuotaExhaustedException quota:
                retry = quota.RetryAfterSeconds;
                break;
            case Autheris.Application.Policy.WriteGrants.WriteBudgetExhaustedException budget:
                retry = budget.RetryAfterSeconds;
                break;
        }

        if (mapped.Status == 503)
        {
            retry ??= 5;
        }

        var hasPosition = mapped.Line is not null && mapped.Column is not null;
        return new McpToolErrorBody(mapped.Code, mapped.Title)
        {
            Hint = mapped.Hint,
            Limit = mapped.Limit,
            Line = hasPosition ? mapped.Line : null,
            Column = hasPosition ? mapped.Column : null,
            EstimatedCost = estimated,
            MaxAllowedCost = maxAllowed,
            RetryAfterSeconds = retry
        };
    }

    /// <summary>A failed result for a SQL parse or analysis failure: the governed code (with position) or a bare syntax error.</summary>
    public static McpToolCallResult FailSql(Exception? failure)
    {
        if (failure is not null && SqlCompilerErrorMapper.TryMap(failure, out var mapped))
        {
            SqlCompilerErrorMapper.RecordOutcome(mapped);
            return FailWith(Serialize(FromSql(failure, mapped)));
        }

        return Fail(GovernedSqlErrorCodes.SyntaxError);
    }

    private static readonly string[] AdminMessageAllowlist =
    [
        "Both 'planId' and 'confirmationToken' are required to apply a confirmed administrator operation.",
        "Confirmation token is required to apply access changes (ADR-03).",
        "Invalid or already consumed TOTP verification code.",
        "Plan confirmation must be performed by the initiating administrator."
    ];

    /// <summary>
    /// A failed admin tool result: the message comes from a closed allowlist of fixed texts; any other text (it may carry principals,
    /// plan ids, datasource names or secrets) is replaced by the fixed message of the code.
    /// </summary>
    public static McpToolCallResult FailAdmin(string? adminErrorText)
    {
        var text = adminErrorText;
        string? fixedText = null;
        if (text is not null)
        {
            foreach (var allowed in AdminMessageAllowlist)
            {
                if (string.Equals(text, allowed, StringComparison.Ordinal))
                {
                    fixedText = allowed;
                    break;
                }
            }

            fixedText ??= text switch
            {
                _ when text.StartsWith("Invalid arguments for admin tool", StringComparison.Ordinal) => "The arguments of the admin tool are invalid.",
                _ when text.StartsWith("Grant policy violation", StringComparison.Ordinal) => "The operation violates the grant policy.",
                _ when text.StartsWith("Unsupported admin tool", StringComparison.Ordinal) => "The admin tool is not supported.",
                _ => null
            };
        }

        return Fail(McpErrorCodes.AdminOperationRejected, fixedText);
    }

    /// <summary>
    /// The one place that turns any failure content into the envelope. Bodies written by this class pass unchanged; every other body
    /// (relayed problem details, JSON-RPC or OData errors, an upstream body that merely looks like the envelope, free text) keeps at
    /// most a catalogue code and its allowed numeric/hint fields and always gets the fixed message of that code. Never throws.
    /// </summary>
    public static string Normalize(string? json)
    {
        try
        {
            if (json is not null && Produced.TryGetValue(json, out _))
            {
                return json;
            }

            return NormalizeForeign(json);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(McpErrorCodes.ExecutionFailed).ContentJson;
        }
    }

    private static string NormalizeForeign(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxNormalizeInputLength)
        {
            return Serialize(McpErrorCodes.ExecutionFailed, McpErrorCodes.FixedMessage(McpErrorCodes.ExecutionFailed));
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fallback();
            }

            JsonElement source;
            string? code = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                source = error;
                code = StringMember(error, "code");
            }
            else if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
            {
                source = root;
                code = StringMember(root, "code");
            }
            else
            {
                return Fallback();
            }

            if (code is null || !CodePattern().IsMatch(code) || !McpErrorCodes.IsCatalogueCode(code))
            {
                return Fallback();
            }

            return Serialize(new McpToolErrorBody(code, McpErrorCodes.FixedMessage(code))
            {
                Hint = StringMember(source, "hint"),
                Line = IntMember(source, "line"),
                Column = IntMember(source, "column"),
                Limit = StringMember(source, "limit"),
                EstimatedCost = IntMember(source, "estimatedCost"),
                MaxAllowedCost = IntMember(source, "maxAllowedCost"),
                RetryAfterSeconds = IntMember(source, "retryAfterSeconds")
            });
        }
        catch (JsonException)
        {
            return Fallback();
        }

        static string Fallback() => Serialize(McpErrorCodes.ExecutionFailed, McpErrorCodes.FixedMessage(McpErrorCodes.ExecutionFailed));
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? IntMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v >= 0 ? v : null;
}
