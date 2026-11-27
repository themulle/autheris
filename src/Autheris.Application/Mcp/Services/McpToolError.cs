namespace Autheris.Application.Mcp.Services;

using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Common;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;

/// <summary>The members of the error envelope (same names and meaning as the WebSQL problem extensions).</summary>
/// <param name="Code">Stable upper-case code.</param>
/// <param name="Message">Text a producer offers for the call. The written <c>message</c> is always the fixed message of the code; this text is
/// kept only as <c>detail</c> for <c>INVALID_PARAMS</c> and <c>INVALID_QUERY</c> (a fixed argument text of the server).</param>
public sealed record McpToolErrorBody(string Code, string Message)
{
    public string? Hint { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Limit { get; init; }
    public int? EstimatedCost { get; init; }
    public int? MaxAllowedCost { get; init; }
    public int? RetryAfterSeconds { get; init; }

    /// <summary>The GraphQL execution result of a GraphQL tool failure; only its filtered errors (message, path, code) are written.</summary>
    public JsonElement? GraphQL { get; init; }
}

/// <summary>
/// The single producer of MCP tool error results: <c>{"error":{"code","message"[,"detail","hint","line","column","limit","estimatedCost",
/// "maxAllowedCost","retryAfterSeconds","graphQL"]}}</c>. The root carries nothing else. <c>message</c> is the fixed message of the code.
/// <see cref="Normalize"/> rebuilds any body from the catalogue code and allowlisted typed members only, so it is idempotent and needs no
/// knowledge of where a body came from.
/// </summary>
public static partial class McpToolError
{
    public const int MaxMessageLength = 256;
    public const int MaxHintLength = 512;
    public const int MaxNormalizeInputLength = 256 * 1024;
    public const string FallbackCode = "ERROR";

    private const int MaxGraphQlErrors = 20;
    private const int MaxGraphQlPathLength = 32;
    private const int MaxServerTexts = 2048;

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    private static partial Regex CodePattern();

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> LimitKinds = new(Enum.GetNames<SqlLimitKind>(), StringComparer.Ordinal);

    // Fixed texts of the server (argument templates and mapper hints), registered when the server writes them. Only a registered text
    // survives Normalize, so a relayed body cannot bring its own detail or hint. Bounded: a producer that wrongly formats request input
    // into a template can neither grow it without limit nor make the text reach a client after the bound.
    private static readonly ConcurrentDictionary<string, byte> ServerTexts = new(StringComparer.Ordinal);

    private static bool AllowsDetail(string code) =>
        code is McpErrorCodes.InvalidParams or McpErrorCodes.InvalidQuery;

    private static bool AllowsHint(string code) =>
        code == McpErrorCodes.TokenBudgetExceeded || McpErrorCodes.IsGovernedSql(code);

    private static bool AllowsGraphQl(string code) =>
        code is McpErrorCodes.ExecutionFailed or McpErrorCodes.QueryTimeout or McpErrorCodes.InvalidQuery;

    private static string? Register(string? text, int maxLength)
    {
        var clean = ErrorText.Sanitize(text, string.Empty, maxLength);
        if (clean.Length == 0)
        {
            return null;
        }

        if (!ServerTexts.ContainsKey(clean) && ServerTexts.Count < MaxServerTexts)
        {
            ServerTexts.TryAdd(clean, 0);
        }

        return clean;
    }

    private static bool IsServerText(string? text) => text is not null && ServerTexts.ContainsKey(text);

    /// <summary>Serializes an envelope of a code and the producer's text (see <see cref="McpToolErrorBody"/>).</summary>
    public static string Serialize(string code, string message) => Serialize(new McpToolErrorBody(code, message));

    /// <summary>Serializes the envelope in its fixed member order; null members are omitted.</summary>
    public static string Serialize(McpToolErrorBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var code = !string.IsNullOrEmpty(body.Code) && CodePattern().IsMatch(body.Code) ? body.Code : FallbackCode;
        var fixedMessage = McpErrorCodes.FixedMessage(code);
        string? detail = null;
        if (AllowsDetail(code) && !string.IsNullOrWhiteSpace(body.Message) && body.Message != fixedMessage)
        {
            detail = Register(body.Message, MaxMessageLength);
        }

        string? hint = AllowsHint(code) ? Register(body.Hint, MaxHintLength) : null;
        var limit = body.Limit is { } l && LimitKinds.Contains(l) ? l : null;
        return Write(code, fixedMessage, detail, hint, body.Line, body.Column, limit, body.EstimatedCost, body.MaxAllowedCost, body.RetryAfterSeconds,
            AllowsGraphQl(code) ? body.GraphQL : null);
    }

    private static string Write(string code, string message, string? detail, string? hint, int? line, int? column, string? limit,
        int? estimatedCost, int? maxAllowedCost, int? retryAfterSeconds, JsonElement? graphQl)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            if (detail is not null) w.WriteString("detail", detail);
            if (hint is not null) w.WriteString("hint", hint);
            if (line is { } ln) w.WriteNumber("line", ln);
            if (column is { } col) w.WriteNumber("column", col);
            if (limit is not null) w.WriteString("limit", limit);
            if (estimatedCost is { } est) w.WriteNumber("estimatedCost", est);
            if (maxAllowedCost is { } max) w.WriteNumber("maxAllowedCost", max);
            if (retryAfterSeconds is { } retry) w.WriteNumber("retryAfterSeconds", retry);
            if (graphQl is { ValueKind: JsonValueKind.Object } gql)
            {
                WriteGraphQlErrors(w, gql);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes <c>graphQL: {errors:[{message, path, extensions:{code}}]}</c> from a GraphQL result: the errors as the /graphql pipeline
    /// (error filter and enumeration shield of the request executor) produced them, reduced to message, path and code. No data,
    /// locations or other extensions.
    /// </summary>
    private static void WriteGraphQlErrors(Utf8JsonWriter w, JsonElement result)
    {
        if (!result.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var written = new List<(string Message, JsonElement? Path, string? Code)>();
        foreach (var error in errors.EnumerateArray())
        {
            if (written.Count >= MaxGraphQlErrors)
            {
                break;
            }

            if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            JsonElement? path = error.TryGetProperty("path", out var p) && IsSafePath(p) ? p : null;
            string? code = error.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
                           && ext.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                           && c.GetString() is { } cs && CodePattern().IsMatch(cs) ? cs : null;
            written.Add((ErrorText.Sanitize(m.GetString(), string.Empty, 512), path, code));
        }

        if (written.Count == 0)
        {
            return;
        }

        w.WriteStartObject("graphQL");
        w.WriteStartArray("errors");
        foreach (var (message, path, code) in written)
        {
            w.WriteStartObject();
            w.WriteString("message", message);
            if (path is { } pe)
            {
                w.WritePropertyName("path");
                pe.WriteTo(w);
            }

            if (code is not null)
            {
                w.WriteStartObject("extensions");
                w.WriteString("code", code);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static bool IsSafePath(JsonElement path)
    {
        if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() > MaxGraphQlPathLength)
        {
            return false;
        }

        foreach (var segment in path.EnumerateArray())
        {
            if (segment.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) || (segment.ValueKind == JsonValueKind.String && segment.GetString()!.Length > 128))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A failed tool result for a code; <paramref name="detail"/> is a fixed argument text (kept for INVALID_PARAMS and INVALID_QUERY only).</summary>
    public static McpToolCallResult Fail(string code, string? detail = null)
    {
        var envelope = Serialize(code, detail ?? McpErrorCodes.FixedMessage(code));
        return new McpToolCallResult(IsSuccess: false, ContentJson: envelope, ErrorMessage: envelope);
    }

    /// <summary>A failed tool result for an envelope that was already built.</summary>
    public static McpToolCallResult FailWith(string envelopeJson) =>
        new(IsSuccess: false, ContentJson: envelopeJson, ErrorMessage: envelopeJson);

    /// <summary>The envelope body of a governed SQL error: the stable code and the position/limit/retry fields of the WebSQL problem.</summary>
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
        return new McpToolErrorBody(mapped.Code, McpErrorCodes.FixedMessage(mapped.Code))
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

    /// <summary>A failed admin tool result: the message is the fixed message of the code; the admin layer's own text stays in the audit row.</summary>
    public static McpToolCallResult FailAdmin(string? adminErrorText) => Fail(McpErrorCodes.AdminOperationRejected);

    /// <summary>
    /// The one place that turns any failure content into the envelope. Any body (own, relayed problem details, JSON-RPC or OData errors,
    /// free text) is re-parsed and rebuilt from its catalogue code and the allowlisted typed members; the message is always the fixed
    /// message of the code. A code outside the catalogue, an unparseable or oversized body gives EXECUTION_FAILED. Idempotent, never throws.
    /// </summary>
    public static string Normalize(string? json)
    {
        try
        {
            return NormalizeCore(json);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fallback();
        }
    }

    private static string Fallback() =>
        Write(McpErrorCodes.ExecutionFailed, McpErrorCodes.FixedMessage(McpErrorCodes.ExecutionFailed), null, null, null, null, null, null, null, null, null);

    private static string NormalizeCore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxNormalizeInputLength)
        {
            return Fallback();
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fallback();
            }

            JsonElement source;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                source = error;
            }
            else if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
            {
                source = root;
            }
            else
            {
                return Fallback();
            }

            var code = StringMember(source, "code");
            if (code is null || !CodePattern().IsMatch(code) || !McpErrorCodes.IsCatalogueCode(code))
            {
                return Fallback();
            }

            var detail = AllowsDetail(code) && StringMember(source, "detail") is { } d && IsServerText(d) ? d : null;
            var hint = AllowsHint(code) && StringMember(source, "hint") is { } h && IsServerText(h) ? h : null;
            var limit = StringMember(source, "limit") is { } l && LimitKinds.Contains(l) ? l : null;
            JsonElement? graphQl = AllowsGraphQl(code) && source.TryGetProperty("graphQL", out var gql) && gql.ValueKind == JsonValueKind.Object ? gql : null;
            return Write(code, McpErrorCodes.FixedMessage(code), detail, hint, IntMember(source, "line"), IntMember(source, "column"), limit,
                IntMember(source, "estimatedCost"), IntMember(source, "maxAllowedCost"), IntMember(source, "retryAfterSeconds"), graphQl);
        }
        catch (JsonException)
        {
            return Fallback();
        }
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? IntMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v >= 0 ? v : null;
}
