namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Server-side enforcement of <c>Mcp:MaxResultRows</c>. The cap is the smaller of the option and any client
/// limit, is applied to the already policy-filtered result and reveals nothing about rows that were cut.
/// </summary>
public static class McpRowCap
{
    public const int DefaultCap = 100;
    public const int MaxCap = 10_000;

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>the option clamped to 1..10000; a misconfigured value (&lt;= 0) means the default, never unbounded.</summary>
    public static int ResolveCap(int option) => option <= 0 ? DefaultCap : Math.Clamp(option, 1, MaxCap);

    /// <summary>a client value can lower the cap, never raise it.</summary>
    public static int Effective(int? requested, int optionCap) => Math.Min(requested ?? optionCap, optionCap);

    /// <summary>The argument that carries a client row limit for a tool, or null when the tool has none.</summary>
    public static string? LimitArgument(string toolName) => toolName.ToLowerInvariant() switch
    {
        "query_dataset" => "limit",
        "call_procedure" => "max_rows",
        "sample_rows" => "count",
        _ => null
    };

    /// <summary>
    /// absent or null means "use the cap". Anything but a positive integer that fits an int (zero, negative, text,
    /// fractions, booleans, overflow) is invalid. <paramref name="value"/> is the parsed argument as the executor stores it.
    /// </summary>
    public static bool TryReadLimit(object? value, out int? limit)
    {
        limit = null;
        switch (value)
        {
            case null:
                return true;
            case int i when i >= 1:
                limit = i;
                return true;
            case long l when l is >= 1 and <= int.MaxValue:
                limit = (int)l;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Trims the first row array (<c>rows</c> or <c>data</c>) of a tool result to <paramref name="cap"/>. Only when rows were cut the result
    /// carries <c>truncated: true</c> and <c>rowCap</c>; the row count and any continuation cursor (which would skip the cut rows) follow.
    /// </summary>
    public static string TrimRows(string json, int cap)
    {
        if (!TryParse(json, out var node) || node is not JsonObject obj)
        {
            return json;
        }

        foreach (var name in new[] { "rows", "data" })
        {
            if (obj[name] is not JsonArray rows || rows.Count <= cap)
            {
                continue;
            }

            Cut(rows, cap);
            foreach (var countName in new[] { "rowCount", "count" })
            {
                if (obj[countName] is not null)
                {
                    obj[countName] = cap;
                }
            }

            // continuation properties of the payloads: "next"/"cursor"/"page" and query_dataset "hasMore".
            obj.Remove("next");
            obj.Remove("cursor");
            obj.Remove("page");
            if (obj["hasMore"] is not null)
            {
                obj["hasMore"] = true;
            }

            Flag(obj, cap);
            return obj.ToJsonString(Relaxed);
        }

        return json;
    }

    private static void Cut(JsonArray array, int cap)
    {
        for (var i = array.Count - 1; i >= cap; i--)
        {
            array.RemoveAt(i);
        }
    }

    private static void Flag(JsonObject obj, int cap)
    {
        obj["truncated"] = true;
        obj["rowCap"] = cap;
    }

    private static bool TryParse(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }
}
