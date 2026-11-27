namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Sql;

/// <summary>
/// Reads the optional <c>parameters</c> argument of the MCP <c>query_sql</c> tool into the same shape POST /api/v1/sql binds
/// (name to <see cref="JsonElement"/>). Only the object shape and duplicate names are checked here; the SQL executor is the
/// single validator of names and values for every entry point.
/// </summary>
internal static class McpSqlParameters
{
    internal const string InvalidMessage = "'parameters' must be an object of uniquely named values.";
    internal const string InvalidValueMessage = "A query parameter value is invalid.";

    /// <summary>
    /// Returns true with <c>null</c> when the argument is absent, JSON null or empty. Returns false when it is not an object
    /// or repeats a name (exactly, or ignoring case and a leading @).
    /// </summary>
    internal static bool TryRead(JsonElement arguments, out IReadOnlyDictionary<string, object?>? parameters)
    {
        parameters = null;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("parameters", out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.Clone()))
            {
                return false;
            }
        }

        if (values.Count == 0)
        {
            return true;
        }

        if (WebSqlParameterValues.HasCaseOnlyDuplicateNames(values))
        {
            return false;
        }

        parameters = values;
        return true;
    }
}
