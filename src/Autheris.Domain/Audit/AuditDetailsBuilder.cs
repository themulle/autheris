using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Domain.Audit;

/// <summary>
/// Strongly typed, injection-safe, redaction-aware builder for audit entry DetailsJson.
/// Enforces data minimization, PII scrubbing, and log-injection prevention (Invariants 2 & 4).
/// </summary>
public sealed class AuditDetailsBuilder
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "pwd",
        "secret",
        "token",
        "access_token",
        "refresh_token",
        "id_token",
        "bearer",
        "authorization",
        "cookie",
        "api_key",
        "apikey",
        "privatekey",
        "key"
    };

    private static readonly Regex ControlCharRegex = new(@"[\r\n\t\0\x1b]", RegexOptions.Compiled);

    private readonly int _maxFieldLength;
    private readonly Dictionary<string, object?> _fields = new(StringComparer.OrdinalIgnoreCase);

    public AuditDetailsBuilder(int maxFieldLength = 1024)
    {
        _maxFieldLength = maxFieldLength > 0 ? maxFieldLength : 1024;
    }

    public AuditDetailsBuilder WithField(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (SensitiveKeys.Contains(key))
        {
            _fields[key] = "[REDACTED]";
            return this;
        }

        if (value is string strValue)
        {
            _fields[key] = SanitizeAndTruncate(strValue);
        }
        else
        {
            _fields[key] = value;
        }

        return this;
    }

    private string SanitizeAndTruncate(string input)
    {
        var sanitized = ControlCharRegex.Replace(input, " ");
        if (sanitized.Length > _maxFieldLength)
        {
            const string suffix = "...[TRUNCATED]";
            int takeLen = Math.Max(0, _maxFieldLength - suffix.Length);
            return string.Concat(sanitized.AsSpan(0, takeLen), suffix);
        }

        return sanitized;
    }

    public string Build()
    {
        return JsonSerializer.Serialize(_fields);
    }
}
