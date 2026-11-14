namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-11 / SEC-OI-508-1, SEC-OI-508-2: closed-world, recursive validation of MCP tool arguments against the declared
/// <see cref="McpToolDefinition.InputJsonSchema"/>. Tools are code defined, so the schema is the contract: everything it does not
/// declare is rejected (<c>additionalProperties</c> is <c>false</c> by default at every level). Every failure carries the fixed code
/// <see cref="InvalidArgumentsCode"/>; the parameter name, the matched rule and the client value are never echoed.
/// The English phrase list is not a security boundary; it only adds a block on top of the schema check.
/// </summary>
public sealed class PersistedToolValidator : IPersistedToolValidator
{
    /// <summary>The only failure text: no parameter name, no matched keyword, no value.</summary>
    public const string InvalidArgumentsCode = "invalid_arguments";

    /// <summary>Maximum size of the raw arguments JSON (UTF-8 bytes).</summary>
    public const int MaxArgumentsBytes = 256 * 1024;

    public const int MaxDepth = 8;
    public const int MaxNodes = 1000;
    public const int MaxStringLength = 64 * 1024;

    private static readonly string[] PromptInjectionKeywords =
    [
        "ignore all previous instructions",
        "ignore previous instructions",
        "disregard all previous instructions",
        "system prompt:",
        "developer override mode",
        "disregard authorization rules",
        "bypass rls",
        "bypass authorization",
        "dump database",
        "reveal all tenant records"
    ];

    public string ComputeOperationHash(string graphQlOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphQlOperation);

        // Normalize whitespace to generate canonical hash
        var normalized = Regex.Replace(graphQlOperation.Trim(), @"\s+", " ");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public bool ValidateToolInvocation(McpToolDefinition tool, JsonElement arguments, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(tool);

        bool valid;
        try
        {
            valid = Validate(tool, arguments);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            // Fail closed on anything the walk cannot interpret.
            valid = false;
        }

        failureReason = valid ? null : InvalidArgumentsCode;
        return valid;
    }

    private static bool Validate(McpToolDefinition tool, JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(tool.InputJsonSchema))
        {
            return false;
        }

        using var schemaDoc = JsonDocument.Parse(tool.InputJsonSchema);
        var schema = schemaDoc.RootElement;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var rootType) ||
            rootType.ValueKind != JsonValueKind.String ||
            rootType.GetString() != "object")
        {
            return false;
        }

        // null / undefined arguments mean "no arguments" and are validated as the empty object (required properties still apply).
        using var emptyDoc = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? JsonDocument.Parse("{}") : null;
        var root = emptyDoc?.RootElement ?? arguments;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(root.GetRawText()) > MaxArgumentsBytes)
        {
            return false;
        }

        var nodes = 0;
        return ValidateValue(root, schema, 0, ref nodes);
    }

    private static bool ValidateValue(JsonElement value, JsonElement schema, int depth, ref int nodes)
    {
        if (depth > MaxDepth || ++nodes > MaxNodes || schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TypeMatches(value, schema))
        {
            return false;
        }

        if (!EnumMatches(value, schema))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ValidateString(value, schema);
            case JsonValueKind.Number:
                return ValidateNumber(value, schema);
            case JsonValueKind.Array:
                return ValidateArray(value, schema, depth, ref nodes);
            case JsonValueKind.Object:
                return ValidateObject(value, schema, depth, ref nodes);
            default:
                return true;
        }
    }

    private static bool TypeMatches(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return true;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return KindMatches(value, type.GetString());
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in type.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && KindMatches(value, candidate.GetString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool KindMatches(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        _ => false
    };

    private static bool IsInteger(JsonElement number)
    {
        if (number.TryGetInt64(out _))
        {
            return true;
        }

        return number.TryGetDecimal(out var d) && d == decimal.Truncate(d);
    }

    private static bool EnumMatches(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("enum", out var allowed))
        {
            return true;
        }

        if (allowed.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var option in allowed.EnumerateArray())
        {
            if (option.ValueKind != value.ValueKind && !(IsBoolean(option) && IsBoolean(value)))
            {
                continue;
            }

            var equal = value.ValueKind switch
            {
                JsonValueKind.String => string.Equals(option.GetString(), value.GetString(), StringComparison.Ordinal),
                JsonValueKind.Number => option.TryGetDecimal(out var o) && value.TryGetDecimal(out var v) && o == v,
                JsonValueKind.True or JsonValueKind.False => option.ValueKind == value.ValueKind,
                JsonValueKind.Null => true,
                _ => false
            };
            if (equal)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBoolean(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool ValidateString(JsonElement value, JsonElement schema)
    {
        var text = value.GetString() ?? string.Empty;
        if (text.Length > MaxStringLength)
        {
            return false;
        }

        if (schema.TryGetProperty("maxLength", out var maxLength))
        {
            if (maxLength.ValueKind != JsonValueKind.Number || !maxLength.TryGetInt32(out var max) || text.Length > max)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var keyword in PromptInjectionKeywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateNumber(JsonElement value, JsonElement schema)
    {
        if (!value.TryGetDecimal(out var number))
        {
            // Beyond decimal range: only acceptable when no bound constrains the value, and then still not (fail closed).
            return !schema.TryGetProperty("minimum", out _) && !schema.TryGetProperty("maximum", out _);
        }

        if (schema.TryGetProperty("minimum", out var minimum))
        {
            if (minimum.ValueKind != JsonValueKind.Number || !minimum.TryGetDecimal(out var min) || number < min)
            {
                return false;
            }
        }

        if (schema.TryGetProperty("maximum", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetDecimal(out var max) || number > max)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateArray(JsonElement value, JsonElement schema, int depth, ref int nodes)
    {
        var items = EmptySchema;
        if (schema.TryGetProperty("items", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            items = declared;
        }
        else if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.True)
        {
            items = OpenValueSchema;
        }

        // Without a declared item schema only scalars pass (unless inside an open additionalProperties:true subtree).
        foreach (var item in value.EnumerateArray())
        {
            if (!ValidateValue(item, items, depth + 1, ref nodes))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement OpenValueSchema = JsonDocument.Parse("""{"additionalProperties":true}""").RootElement.Clone();

    private static bool ValidateObject(JsonElement value, JsonElement schema, int depth, ref int nodes)
    {
        JsonElement properties = default;
        var hasProperties = false;
        if (schema.TryGetProperty("properties", out properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            hasProperties = true;
        }

        JsonElement additional = default;
        var additionalAllowed = false;
        var additionalIsSchema = false;
        if (schema.TryGetProperty("additionalProperties", out additional))
        {
            switch (additional.ValueKind)
            {
                case JsonValueKind.True:
                    additionalAllowed = true;
                    break;
                case JsonValueKind.False:
                    break;
                case JsonValueKind.Object:
                    additionalAllowed = true;
                    additionalIsSchema = true;
                    break;
                default:
                    return false;
            }
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            // Duplicate names (exact or differing only in case) are ambiguous: the first-wins/last-wins reading differs per consumer.
            if (!exact.Add(property.Name) || !folded.Add(property.Name))
            {
                return false;
            }

            JsonElement propertySchema;
            if (hasProperties && properties.TryGetProperty(property.Name, out propertySchema))
            {
                // JsonElement.TryGetProperty compares ordinally (case-sensitive).
            }
            else if (additionalIsSchema)
            {
                propertySchema = additional;
            }
            else if (additionalAllowed)
            {
                propertySchema = OpenValueSchema;
            }
            else
            {
                return false;
            }

            if (!ValidateValue(property.Value, propertySchema, depth + 1, ref nodes))
            {
                return false;
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || !exact.Contains(name.GetString()!))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
