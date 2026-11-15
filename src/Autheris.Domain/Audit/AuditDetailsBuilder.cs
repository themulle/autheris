using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Domain.Audit;

/// <summary>
/// Strongly typed, injection-safe, redaction-aware builder and chokepoint sanitizer for audit entry DetailsJson.
/// Enforces data minimization, PII/credential scrubbing, DoS bounds, and log-injection prevention.
/// </summary>
public sealed partial class AuditDetailsBuilder
{
    public const int MaxDepth = 4;
    public const int MaxCollectionElements = 64;
    public const int MaxTotalNodes = 256;
    public const int MaxDetailsBytes = 64 * 1024;
    public const string RedactedPlaceholder = "[REDACTED]";
    public const string TruncatedPlaceholder = "[TRUNCATED]";
    public const string UnparseableJsonMarker = "{\"redaction\":\"unparseable\"}";
    public const string TruncatedJsonMarker = "{\"redaction\":\"truncated\"}";

    private static readonly string[] SensitiveSubstrings =
    [
        "secret",
        "token",
        "password",
        "passwd",
        "pwd",
        "apikey",
        "key",
        "cookie",
        "authorization",
        "bearer",
        "credential",
        "connectionstring",
        "privatekey"
    ];

    private static readonly HashSet<string> AllowListedNormalizedKeys = new(StringComparer.Ordinal)
    {
        "keyset",
        "keyref",
        "primarykey",
        "keycolumns",
        "idempotencykeyhash"
    };

    private readonly int _maxFieldLength;
    private readonly Dictionary<string, object?> _fields = new(StringComparer.OrdinalIgnoreCase);
    private int _builderNodeCount;

    public AuditDetailsBuilder(int maxFieldLength = 1024)
    {
        _maxFieldLength = maxFieldLength > 0 ? maxFieldLength : 1024;
    }

    public AuditDetailsBuilder WithField(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string safeKey = SanitizeAndTruncate(key, _maxFieldLength);

        if (_fields.Count >= MaxCollectionElements)
        {
            _fields["_truncated"] = TruncatedPlaceholder;
            return this;
        }

        if (IsSensitiveKey(key))
        {
            _fields[safeKey] = RedactedPlaceholder;
            return this;
        }

        var ctx = new TraversalState(_maxFieldLength) { Nodes = _builderNodeCount };
        _fields[safeKey] = RedactDotNetValue(value, depth: 1, ctx);
        _builderNodeCount = ctx.Nodes;
        return this;
    }

    public string Build()
    {
        var json = JsonSerializer.Serialize(_fields);
        return Encoding.UTF8.GetByteCount(json) > MaxDetailsBytes ? TruncatedJsonMarker : json;
    }

    public static string RedactJson(string? detailsJson, Func<string, string>? stringScrubber = null, int maxFieldLength = 1024)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return "{}";
        }

        if (Encoding.UTF8.GetByteCount(detailsJson) > MaxDetailsBytes)
        {
            return TruncatedJsonMarker;
        }

        int effectiveMaxLen = maxFieldLength > 0 ? maxFieldLength : 1024;
        var ctx = new TraversalState(effectiveMaxLen, stringScrubber);

        try
        {
            using var doc = JsonDocument.Parse(detailsJson, new JsonDocumentOptions { MaxDepth = 256 });
            object? sanitized = doc.RootElement.ValueKind == JsonValueKind.Object
                ? RedactJsonObject(doc.RootElement, depth: 0, ctx)
                : RedactJsonElement(doc.RootElement, depth: 0, ctx);

            string output = JsonSerializer.Serialize(sanitized);
            return Encoding.UTF8.GetByteCount(output) > MaxDetailsBytes ? TruncatedJsonMarker : output;
        }
        catch (JsonException)
        {
            return UnparseableJsonMarker;
        }
    }

    public static string? ScrubValue(string? value) =>
        value == null ? null : ScrubAndSanitize(value, maxFieldLength: 1024, externalScrubber: null);

    public static bool IsSensitiveKey(string key)
    {
        string forAllowList = NormalizeKeyForAllowList(key);
        if (AllowListedNormalizedKeys.Contains(forAllowList))
        {
            return false;
        }

        string alnum = NormalizeAlphaNumeric(key);
        foreach (var sub in SensitiveSubstrings)
        {
            if (forAllowList.Contains(sub, StringComparison.Ordinal) || alnum.Contains(sub, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeKeyForAllowList(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (c == '-' || c == '_' || c == '.') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string NormalizeAlphaNumeric(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static Dictionary<string, object?> RedactJsonObject(JsonElement obj, int depth, TraversalState ctx)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? lastKey = null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (!ctx.TryConsumeNode())
            {
                if (result.Count >= MaxCollectionElements && lastKey != null) result.Remove(lastKey);
                result["_truncated"] = TruncatedPlaceholder;
                break;
            }

            if (result.Count >= MaxCollectionElements)
            {
                if (lastKey != null) result.Remove(lastKey);
                result["_truncated"] = TruncatedPlaceholder;
                break;
            }

            string safeKey = SanitizeAndTruncate(prop.Name, ctx.MaxFieldLength);
            if (IsSensitiveKey(prop.Name))
            {
                result[safeKey] = RedactedPlaceholder;
            }
            else
            {
                result[safeKey] = RedactJsonElement(prop.Value, depth + 1, ctx);
            }

            lastKey = safeKey;
        }

        return result;
    }

    private static object? RedactJsonElement(JsonElement element, int depth, TraversalState ctx)
    {
        if (depth >= MaxDepth && (element.ValueKind == JsonValueKind.Object || element.ValueKind == JsonValueKind.Array))
        {
            return TruncatedPlaceholder;
        }

        if (depth > MaxDepth)
        {
            return TruncatedPlaceholder;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return RedactJsonObject(element, depth, ctx);
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    if (!ctx.TryConsumeNode() || list.Count >= MaxCollectionElements)
                    {
                        if (list.Count >= MaxCollectionElements) list[^1] = TruncatedPlaceholder;
                        else list.Add(TruncatedPlaceholder);
                        break;
                    }
                    list.Add(RedactJsonElement(item, depth + 1, ctx));
                }
                return list;
            case JsonValueKind.String:
                return ScrubAndSanitize(element.GetString() ?? string.Empty, ctx.MaxFieldLength, ctx.ExternalScrubber);
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.Clone();
            default:
                return null;
        }
    }

    private static object? RedactDotNetValue(object? value, int depth, TraversalState ctx)
    {
        if (value == null) return null;

        if (!ctx.TryConsumeNode() || depth > MaxDepth)
        {
            return TruncatedPlaceholder;
        }

        switch (value)
        {
            case string s:
                return ScrubAndSanitize(s, ctx.MaxFieldLength, ctx.ExternalScrubber);
            case JsonElement je:
                return RedactJsonElement(je, depth, ctx);
            case IDictionary dict:
                if (depth >= MaxDepth) return TruncatedPlaceholder;
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? lastKey = null;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!ctx.TryConsumeNode() || obj.Count >= MaxCollectionElements)
                    {
                        if (obj.Count >= MaxCollectionElements && lastKey != null) obj.Remove(lastKey);
                        obj["_truncated"] = TruncatedPlaceholder;
                        break;
                    }
                    string rawKey = Convert.ToString(entry.Key) ?? string.Empty;
                    string safeKey = SanitizeAndTruncate(rawKey, ctx.MaxFieldLength);
                    obj[safeKey] = IsSensitiveKey(rawKey)
                        ? RedactedPlaceholder
                        : RedactDotNetValue(entry.Value, depth + 1, ctx);
                    lastKey = safeKey;
                }
                return obj;
            case IEnumerable seq when value is not byte[]:
                if (depth >= MaxDepth) return TruncatedPlaceholder;
                var items = new List<object?>();
                foreach (var item in seq)
                {
                    if (!ctx.TryConsumeNode() || items.Count >= MaxCollectionElements)
                    {
                        if (items.Count >= MaxCollectionElements) items[^1] = TruncatedPlaceholder;
                        else items.Add(TruncatedPlaceholder);
                        break;
                    }
                    items.Add(RedactDotNetValue(item, depth + 1, ctx));
                }
                return items;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid or DateTime or DateTimeOffset:
                return value;
            case Enum e:
                return SanitizeAndTruncate(e.ToString(), ctx.MaxFieldLength);
            default:
                try
                {
                    var elem = JsonSerializer.SerializeToElement(value);
                    return RedactJsonElement(elem, depth, ctx);
                }
                catch
                {
                    return ScrubAndSanitize(value.ToString() ?? string.Empty, ctx.MaxFieldLength, ctx.ExternalScrubber);
                }
        }
    }

    private static string ScrubAndSanitize(string input, int maxFieldLength, Func<string, string>? externalScrubber)
    {
        string value = externalScrubber != null ? externalScrubber(input) : input;
        value = ScrubSensitiveStrings(value);
        return SanitizeAndTruncate(value, maxFieldLength);
    }

    private static string ScrubSensitiveStrings(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var scrubbed = BearerTokenRegex().Replace(value, "$1" + RedactedPlaceholder);
        scrubbed = JwtRegex().Replace(scrubbed, RedactedPlaceholder);
        scrubbed = UrlCredentialsRegex().Replace(scrubbed, "$1" + RedactedPlaceholder + "@");
        scrubbed = ConnectionPasswordRegex().Replace(scrubbed, "$1" + RedactedPlaceholder);
        scrubbed = KeyValueSecretRegex().Replace(scrubbed, "$1" + RedactedPlaceholder);
        scrubbed = AwsKeyRegex().Replace(scrubbed, RedactedPlaceholder);
        return scrubbed;
    }

    private static string SanitizeAndTruncate(string input, int maxFieldLength)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
        }

        string sanitized = sb.ToString();
        if (sanitized.Length > maxFieldLength)
        {
            const string suffix = "...[TRUNCATED]";
            int takeLen = Math.Max(0, maxFieldLength - suffix.Length);
            return string.Concat(sanitized.AsSpan(0, takeLen), suffix);
        }

        return sanitized;
    }

    [GeneratedRegex(@"(Bearer\s+)[A-Za-z0-9\-\._~\+\/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])eyJ[A-Za-z0-9_\-]{3,}\.[A-Za-z0-9_\-]{3,}\.[A-Za-z0-9_\-]{3,}(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+\-.]*://)[^\s/""'<>]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialsRegex();

    [GeneratedRegex(@"((?:password|pwd)\s*=\s*)(""[^""]*""|'[^']*'|[^;\s,""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionPasswordRegex();

    [GeneratedRegex(@"((?:api[_-]?key|token|secret)\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s;,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"\b(AKIA[0-9A-Z]{16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKeyRegex();

    private sealed class TraversalState(int maxFieldLength, Func<string, string>? externalScrubber = null)
    {
        public int MaxFieldLength { get; } = maxFieldLength;
        public Func<string, string>? ExternalScrubber { get; } = externalScrubber;
        public int Nodes;

        public bool TryConsumeNode()
        {
            if (Nodes >= MaxTotalNodes) return false;
            Nodes++;
            return true;
        }
    }
}
