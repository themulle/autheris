namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// Issue #702: serializer options of MCP tool results. A member whose value is <c>null</c> or an object without members
/// (an empty dictionary, or an object whose members are all omitted) is not written: it costs the client context and
/// carries no information. Arrays are always written, also when empty, and dictionary entries (row values) are never
/// omitted. A member marked <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c> is always written.
/// </summary>
public static class McpResultJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { info => OmitEmptyMembers(info, options) } };
        options.MakeReadOnly();
        return options;
    }

    private static void OmitEmptyMembers(JsonTypeInfo info, JsonSerializerOptions options)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            // An explicit [JsonIgnore] (Never, Always or a condition) keeps its own behaviour.
            if (property.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true)
            {
                continue;
            }

            var inner = property.ShouldSerialize;
            property.ShouldSerialize = (owner, value) =>
                value is not null && (inner is null || inner(owner, value)) && !IsEmptyObject(value, options);
        }
    }

    // The check recurses through the ShouldSerialize predicates of nested members. A cyclic graph must not overflow the
    // stack before the serializer reports the cycle itself, so beyond MaxDepth a value counts as not empty.
    [ThreadStatic]
    private static int _emptyCheckDepth;

    /// <summary>True for a dictionary without entries and for an object none of whose members would be written.</summary>
    private static bool IsEmptyObject(object value, JsonSerializerOptions options)
    {
        if (value is string or ValueType)
        {
            return false;
        }

        var info = options.GetTypeInfo(value.GetType());
        if (info.Kind == JsonTypeInfoKind.Dictionary)
        {
            return value is ICollection { Count: 0 };
        }

        if (info.Kind != JsonTypeInfoKind.Object || _emptyCheckDepth >= MaxDepth(options))
        {
            return false;
        }

        _emptyCheckDepth++;
        try
        {
            foreach (var property in info.Properties)
            {
                if (property.Get is null)
                {
                    continue;
                }

                var member = property.Get(value);
                if (property.ShouldSerialize is { } shouldSerialize ? shouldSerialize(value, member) : member is not null || IsNeverIgnored(property))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _emptyCheckDepth--;
        }
    }

    private static int MaxDepth(JsonSerializerOptions options) => options.MaxDepth > 0 ? options.MaxDepth : 64;

    private static bool IsNeverIgnored(JsonPropertyInfo property) =>
        property.AttributeProvider is ICustomAttributeProvider provider &&
        provider.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true) is [JsonIgnoreAttribute { Condition: JsonIgnoreCondition.Never }, ..];
}
