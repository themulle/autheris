namespace Autheris.Application.Diagnostics.Shadowing;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// F-OPS-01: High-throughput PII and credential redactor for dark traffic shadowing.
/// Sanitizes authentication tokens, cookies, and sensitive customer data before replay.
/// </summary>
public static class PiiShadowingRedactor
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
    private static readonly Regex JsonEmbeddedStringLiteralRegex = new(@"\\""(?:[^""\\]|\\[^""])*\\""", RegexOptions.Compiled);

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
    /// EXT-3: query string values (e.g. OData <c>$filter=email eq 'x'</c>) are redacted like the body; the path is kept.
    /// </summary>
    public static string RedactPathAndQuery(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (q < 0 || q == pathAndQuery.Length - 1)
        {
            return pathAndQuery;
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

            parts[i] = parts[i][..(eq + 1)] + Uri.EscapeDataString(RedactBody(value) ?? string.Empty);
        }

        return pathAndQuery[..(q + 1)] + string.Join('&', parts);
    }

    public static string? RedactBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var result = EmailRegex.Replace(body, "***@redacted.local");
        result = CreditCardRegex.Replace(result, "****-****-****-****");
        result = SsnRegex.Replace(result, "***-**-****");

        // EXT-3: replace literals structurally, keeping the statement shape for the replay.
        result = SqlStringLiteralRegex.Replace(result, "'***'");
        result = JsonEmbeddedStringLiteralRegex.Replace(result, "\\\"***\\\"");

        return result;
    }
}
