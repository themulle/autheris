namespace Autheris.Application.Diagnostics.Shadowing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using TrinoSqlEngine.Parsing;
using HotChocolate.Language;
using TrinoSqlEngine;

/// <summary>
/// F-OPS-01: High-throughput PII and credential redactor for dark traffic shadowing.
/// Sanitizes authentication tokens, cookies, and sensitive customer data before replay.
/// </summary>
public static partial class PiiShadowingRedactor
{
    private static readonly Regex EmailRegex = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex CreditCardRegex = new(
        @"\b(?:\d{4}[-\s]?){3}\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex SsnRegex = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// EXT-3 / API-12: headers are forwarded by allowlist. Identity and tenant headers (X-Forwarded-User/Tenant,
    /// X-Tenant-ID, test-auth headers, cookies, client certificates) never reach the shadow target.
    /// </summary>
    private static readonly HashSet<string> ForwardedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Content-Type",
        "User-Agent",
        "GraphQL-Preflight",
        "traceparent",
        "tracestate",
        "X-Request-Id",
        "X-Correlation-Id"
    };

    /// <summary>SQL string literal ('...' with '' escapes) and GraphQL string literal inside a JSON body (\"...\").</summary>
    private static readonly Regex SqlStringLiteralRegex = new(@"'(?:[^']|'')*'", RegexOptions.Compiled);

    public static Dictionary<string, string> RedactHeaders(
        IReadOnlyDictionary<string, string> incomingHeaders,
        bool stripPiiHeaders)
    {
        var sanitized = new Dictionary<string, string>(incomingHeaders.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in incomingHeaders)
        {
            if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                // Replace production credentials with synthetic staging replay token
                sanitized[key] = "Bearer staging-shadow-synthetic-token";
                continue;
            }

            if (ForwardedHeaders.Contains(key))
            {
                sanitized[key] = value;
            }
        }

        return sanitized;
    }

    /// <summary>
    /// EXT-3 / MISC-14: query string values (e.g. OData <c>$filter=email eq 'x'</c>) are redacted like the body. Every path segment that is
    /// not in the allowlist of static route segments becomes <c>_</c> (REST ids, OData keys, tenant or customer names in the path);
    /// OData key segments <c>x(...)</c> become <c>x(_)</c>.
    /// </summary>
    /// <param name="pathAndQuery">The request path and query string.</param>
    /// <param name="allowedPathSegments">Static route segments to keep; <c>null</c> uses <see cref="Autheris.Domain.Options.TrafficShadowingOptions.DefaultAllowedPathSegments"/>.</param>
    public static string RedactPathAndQuery(string pathAndQuery, IReadOnlyCollection<string>? allowedPathSegments = null)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = RedactPath(q < 0 ? pathAndQuery : pathAndQuery[..q], allowedPathSegments ?? Autheris.Domain.Options.TrafficShadowingOptions.DefaultAllowedPathSegments);
        if (q < 0 || q == pathAndQuery.Length - 1)
        {
            return path + (q < 0 ? string.Empty : pathAndQuery[q..]);
        }

        var parts = pathAndQuery[(q + 1)..].Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(parts[i][(eq + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                value = string.Empty;
            }

            parts[i] = parts[i][..(eq + 1)] + Uri.EscapeDataString(RedactQueryValue(value));
        }

        return path + "?" + string.Join('&', parts);
    }

    private static string RedactPath(string path, IReadOnlyCollection<string> allowed)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            var paren = segment.IndexOf('(', StringComparison.Ordinal);
            var name = paren < 0 ? segment : segment[..paren];
            var keptName = name.Length > 0 && allowed.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : "_";
            segments[i] = paren < 0 ? keptName : keptName + "(_)";
        }

        return string.Join('/', segments);
    }

    /// <summary>A query-string value: expressions (several tokens) are literal-redacted by the SQL lexer, scalars keep the legacy PII patterns.</summary>
    private static string RedactQueryValue(string value)
    {
        var tokens = TryLexSql(value);
        if (tokens is { Count: > 1 } && TryRedactSqlTokens(value, tokens, out var redactedExpression))
        {
            return RedactPiiPatterns(redactedExpression);
        }

        return RedactPiiPatterns(SqlStringLiteralRegex.Replace(value, "'***'"));
    }

    private static JsonNode? RedactLeafValue(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                obj[key] = RedactLeafValue(obj[key]);
            }
            return obj;
        }
        if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                arr[i] = RedactLeafValue(arr[i]);
            }
            return arr;
        }
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var strVal))
            {
                if (string.IsNullOrWhiteSpace(strVal)) return JsonValue.Create(strVal);
                var redacted = EmailRegex.Replace(strVal, "***@redacted.local");
                redacted = CreditCardRegex.Replace(redacted, "****-****-****-****");
                redacted = SsnRegex.Replace(redacted, "***-**-****");
                if (redacted == strVal)
                {
                    return JsonValue.Create("***");
                }
                return JsonValue.Create(redacted);
            }
            if (val.TryGetValue<double>(out _) || val.TryGetValue<long>(out _) || val.TryGetValue<int>(out _) || val.TryGetValue<decimal>(out _))
            {
                return JsonValue.Create(0);
            }
            return val.DeepClone();
        }
        return node;
    }

    private static string RedactPiiPatterns(string text)
    {
        var result = EmailRegex.Replace(text, "***@redacted.local");
        result = CreditCardRegex.Replace(result, "****-****-****-****");
        return SsnRegex.Replace(result, "***-**-****");
    }

    /// <summary>
    /// MISC-14: a query text is a GraphQL document or SQL. GraphQL is parsed (every int, float, string, boolean and enum value becomes a
    /// placeholder); SQL is tokenised with the engine lexer (every numeric and string literal becomes a placeholder). Text that is neither
    /// returns false: the request must not be shadowed.
    /// </summary>
    private static bool TryRedactQueryText(string text, out string redacted)
    {
        if (TryRedactGraphQl(text, out redacted))
        {
            redacted = RedactPiiPatterns(redacted);
            return true;
        }

        var tokens = TryLexSql(text);
        if (tokens != null && LooksLikeSql(tokens) && TryRedactSqlTokens(text, tokens, out redacted))
        {
            redacted = RedactPiiPatterns(redacted);
            return true;
        }

        redacted = string.Empty;
        return false;
    }

    private static bool TryRedactGraphQl(string text, out string redacted)
    {
        redacted = string.Empty;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var document = Utf8GraphQLParser.Parse(bytes);
            var edits = new List<(int Start, int End, string Replacement)>();
            CollectGraphQlLiterals(document, edits);

            // Replace back to front on the UTF-8 bytes the parser positions refer to.
            var buffer = new List<byte>(bytes);
            foreach (var (start, _, replacement) in edits.OrderByDescending(e => e.Start))
            {
                // Node end positions are unreliable in HotChocolate.Language (they can run into the next token), so only the
                // start offset is used and the literal's own token is re-read to find where it ends.
                var end = start < 0 || start >= bytes.Length ? -1 : start + LiteralLength(bytes.AsSpan(start));
                if (end <= start || end > buffer.Count)
                {
                    return false;
                }

                buffer.RemoveRange(start, end - start);
                buffer.InsertRange(start, Encoding.UTF8.GetBytes(replacement));
            }

            var candidate = Encoding.UTF8.GetString(buffer.ToArray());
            // The result must still be a valid document (fail closed on any position mismatch).
            _ = Utf8GraphQLParser.Parse(candidate);
            redacted = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int LiteralLength(ReadOnlySpan<byte> fromLiteralStart)
    {
        var reader = new Utf8GraphQLReader(fromLiteralStart);
        while (reader.Read())
        {
            if (reader.Kind != TokenKind.StartOfFile)
            {
                // The reader's string tokens end at the closing quote(s), not after them.
                return reader.Kind switch
                {
                    TokenKind.String => reader.End + 1,
                    TokenKind.BlockString => reader.End + 3,
                    _ => reader.End
                };
            }
        }

        return -1;
    }

    private static void CollectGraphQlLiterals(ISyntaxNode node, List<(int Start, int End, string Replacement)> edits)
    {
        string? replacement = node switch
        {
            IntValueNode => "0",
            FloatValueNode => "0.0",
            StringValueNode => "\"***\"",
            BooleanValueNode => "false",
            EnumValueNode => "REDACTED",
            _ => null
        };

        if (replacement != null)
        {
            if (node.Location is { } location)
            {
                edits.Add((location.Start, location.End, replacement));
            }
            else
            {
                throw new InvalidOperationException("GraphQL literal without a source location.");
            }

            return;
        }

        foreach (var child in node.GetNodes())
        {
            CollectGraphQlLiterals(child, edits);
        }
    }

    private static readonly HashSet<string> SqlStatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "VALUES", "TABLE", "SHOW", "DESCRIBE", "EXPLAIN"
    };

    /// <summary>Only statements that start with a SQL statement keyword count as SQL; anything else (e.g. a truncated GraphQL document) is not shadowed.</summary>
    private static bool LooksLikeSql(List<IToken> tokens)
    {
        var first = tokens.FirstOrDefault(t => t.Type is not (SqlBaseLexer.SIMPLE_COMMENT or SqlBaseLexer.BRACKETED_COMMENT));
        return first?.Text is { } keyword && SqlStatementKeywords.Contains(keyword);
    }

    /// <summary>The SQL lexer tokens of <paramref name="text"/>, or null when it contains anything the lexer does not recognise.</summary>
    private static List<IToken>? TryLexSql(string text)
    {
        try
        {
            var lexer = AntlrCacheGenerations.Shared.CreateLexer(new ZeroCopyCaseInsensitiveStream(text.AsMemory()));
            lexer.RemoveErrorListeners();
            var tokens = new List<IToken>();
            foreach (var token in lexer.GetAllTokens())
            {
                if (token.Type == SqlBaseLexer.UNRECOGNIZED)
                {
                    return null;
                }

                if (token.Channel == TokenConstants.DefaultChannel || token.Type is SqlBaseLexer.SIMPLE_COMMENT or SqlBaseLexer.BRACKETED_COMMENT)
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryRedactSqlTokens(string text, List<IToken> tokens, out string redacted)
    {
        var sb = new StringBuilder(text.Length);
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.StartIndex < position || token.StopIndex >= text.Length)
            {
                redacted = string.Empty;
                return false;
            }

            string? replacement = token.Type switch
            {
                SqlBaseLexer.STRING or SqlBaseLexer.UNICODE_STRING or SqlBaseLexer.DOLLAR_STRING or SqlBaseLexer.BINARY_LITERAL => "'***'",
                SqlBaseLexer.INTEGER_VALUE or SqlBaseLexer.DECIMAL_VALUE or SqlBaseLexer.DOUBLE_VALUE => "0",
                SqlBaseLexer.SIMPLE_COMMENT or SqlBaseLexer.BRACKETED_COMMENT => " ",
                _ => null
            };

            sb.Append(text, position, token.StartIndex - position);
            sb.Append(replacement ?? text.Substring(token.StartIndex, token.StopIndex - token.StartIndex + 1));
            position = token.StopIndex + 1;
        }

        sb.Append(text, position, text.Length - position);
        redacted = sb.ToString();
        return true;
    }

    private static readonly System.Text.Json.JsonSerializerOptions UnsafeRelaxedOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The redacted body, or <c>"***"</c> when a query text could not be parsed (never the original). Callers that must drop such a request
    /// use <see cref="TryRedactBody"/>.
    /// </summary>
    public static string? RedactBody(string? body) => TryRedactBody(body, out var redacted) ? redacted : "***";

    /// <summary>
    /// MISC-14: redacts a request body. Returns false (and no body) when a GraphQL or SQL text cannot be parsed: such a request is not shadowed.
    /// </summary>
    public static bool TryRedactBody(string? body, out string? redacted)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            redacted = body;
            return true;
        }

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            JsonNode? root = null;
            try
            {
                root = JsonNode.Parse(body);
            }
            catch (Exception)
            {
                // Not JSON: handled as query text below
            }

            if (root is JsonObject obj)
            {
                var keys = obj.Select(kv => kv.Key).ToList();
                foreach (var key in keys)
                {
                    if (string.Equals(key, "operationName", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(key, "query", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "sql", StringComparison.OrdinalIgnoreCase))
                    {
                        if (obj[key] is JsonValue jVal && jVal.TryGetValue<string>(out var queryText))
                        {
                            if (!TryRedactQueryText(queryText, out var redactedQuery))
                            {
                                redacted = null;
                                return false;
                            }

                            obj[key] = JsonValue.Create(redactedQuery);
                        }
                        else
                        {
                            obj[key] = RedactLeafValue(obj[key]);
                        }
                    }
                    else
                    {
                        obj[key] = RedactLeafValue(obj[key]);
                    }
                }

                redacted = obj.ToJsonString(UnsafeRelaxedOptions);
                return true;
            }

            if (root is JsonArray arr)
            {
                redacted = RedactLeafValue(arr)?.ToJsonString(UnsafeRelaxedOptions);
                return true;
            }
        }

        if (!TryRedactQueryText(body, out var text))
        {
            redacted = null;
            return false;
        }

        redacted = text;
        return true;
    }
}
