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

    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "ApiKey",
        "api-key",
        "Proxy-Authorization",
        "X-Forwarded-Client-Cert",
        "Client-Cert",
        "X-Arr-ClientCert",
        "X-Client-Cert",
        "X-Vault-Token",
        "X-Auth-Token"
    };

    public static Dictionary<string, string> RedactHeaders(
        IReadOnlyDictionary<string, string> incomingHeaders,
        bool stripPiiHeaders)
    {
        var sanitized = new Dictionary<string, string>(incomingHeaders.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in incomingHeaders)
        {
            if (DroppedHeaders.Contains(key))
            {
                continue;
            }

            if (stripPiiHeaders && (key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                                    key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                    key.Contains("password", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                // Replace production credentials with synthetic staging replay token
                sanitized[key] = "Bearer staging-shadow-synthetic-token";
                continue;
            }

            sanitized[key] = value;
        }

        return sanitized;
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

        return result;
    }
}
