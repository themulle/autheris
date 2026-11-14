using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Domain.Audit;

/// <summary>
/// Strongly typed, injection-safe, redaction-aware builder for audit entry DetailsJson.
/// Enforces data minimization, PII scrubbing, and log-injection prevention (Invariants 2 &amp; 4).
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

    public static string RedactJson(string? detailsJson, Func<string, string>? stringScrubber = null, int maxFieldLength = 1024)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RedactScalarOrArray(doc.RootElement, stringScrubber, maxFieldLength);
            }

            var sanitized = RedactObject(doc.RootElement, stringScrubber, maxFieldLength);
            return JsonSerializer.Serialize(sanitized);
        }
        catch (JsonException)
        {
            var builder = new AuditDetailsBuilder(maxFieldLength);
            var scrubbed = stringScrubber != null ? stringScrubber(detailsJson) : detailsJson;
            return builder.WithField("raw", scrubbed).Build();
        }
    }

    private static string RedactScalarOrArray(JsonElement element, Func<string, string>? stringScrubber, int maxFieldLength)
    {
        var value = RedactElement(element, stringScrubber, maxFieldLength);
        return JsonSerializer.Serialize(value);
    }

    private static Dictionary<string, object?> RedactObject(JsonElement obj, Func<string, string>? stringScrubber, int maxFieldLength)
    {
        var builder = new AuditDetailsBuilder(maxFieldLength);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            if (SensitiveKeys.Contains(prop.Name))
            {
                result[prop.Name] = "[REDACTED]";
                continue;
            }

            result[prop.Name] = RedactElement(prop.Value, stringScrubber, maxFieldLength, builder);
        }

        return result;
    }

    private static object? RedactElement(JsonElement element, Func<string, string>? stringScrubber, int maxFieldLength, AuditDetailsBuilder? builder = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return RedactObject(element, stringScrubber, maxFieldLength);
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(RedactElement(item, stringScrubber, maxFieldLength, builder));
                }
                return list;
            case JsonValueKind.String:
                var str = element.GetString() ?? string.Empty;
                if (stringScrubber != null)
                {
                    str = stringScrubber(str);
                }
                builder ??= new AuditDetailsBuilder(maxFieldLength);
                return builder.SanitizeAndTruncate(str);
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.Clone();
            default:
                return null;
        }
    }
}
