using System.Text.Json;

namespace Autheris.Domain.Common;

/// <summary>
/// Converts arbitrary JSON values (strings, booleans, numbers, null, nested objects) to their plain string form,
/// e.g. for dbt/OpenAPI <c>meta</c> maps where <c>{"pii": false}</c> must not fail a string-only read.
/// </summary>
public static class JsonValueText
{
    public static string From(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText()
        };
}
