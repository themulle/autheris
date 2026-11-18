namespace Autheris.Application.Mcp.Services;

using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Shortens an over-budget catalog result at structure boundaries instead of replacing it with a notice: whole datasets for
/// <c>list_datasets</c> and <c>search_catalog</c>, whole columns for <c>describe_dataset</c> (issue #820). Only parameters that the
/// tools really have are named in the note: <c>offset</c>/<c>limit</c> for <c>list_datasets</c>, <c>limit</c> for
/// <c>search_catalog</c>, <c>columnOffset</c>/<c>columns</c>/<c>brief</c> for <c>describe_dataset</c>.
/// </summary>
internal static class McpBudgetShortening
{
    private const string DatasetsProperty = "datasets";
    private const string ColumnsProperty = "columns";
    private const string ColumnTotalProperty = "columnTotal";

    /// <summary>The shortened answer keeps the relaxed encoder of the unshortened one (<see cref="McpResultJson"/>).</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool AppliesTo(string toolName) =>
        string.Equals(toolName, McpDatasetTools.ListDatasets, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shortens without call arguments; a <c>describe_dataset</c> result is then never shortened, its cursor depends on them.</summary>
    public static bool TryShorten(string toolName, string json, int maxChars, int maxTokens, out string shortened) =>
        TryShorten(toolName, json, argumentsJson: null, maxChars, maxTokens, out shortened);

    /// <summary>
    /// Keeps the longest prefix of the <c>datasets</c> array (list, search) or of the <c>columns</c> array (describe) whose serialization
    /// fits <paramref name="maxChars"/>; every other member is kept unchanged. Returns false when the result has no such array, not even one
    /// item fits (an empty answer with an unchanged cursor would make a client loop), or (describe) <paramref name="argumentsJson"/> is
    /// missing or cannot be read, because the continuation depends on it.
    /// </summary>
    public static bool TryShorten(string toolName, string json, string? argumentsJson, int maxChars, int maxTokens, out string shortened)
    {
        shortened = json;
        if (!AppliesTo(toolName) || ParseObject(json) is not { } root)
        {
            return false;
        }

        var result = string.Equals(toolName, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase)
            ? ShortenColumns(root, argumentsJson, maxChars, maxTokens)
            : ShortenDatasets(root, string.Equals(toolName, McpDatasetTools.ListDatasets, StringComparison.OrdinalIgnoreCase), maxChars, maxTokens);
        if (result is null)
        {
            return false;
        }

        shortened = result;
        return true;
    }

    /// <summary>
    /// The note of the notice that replaces a result which cannot be shortened. For <c>describe_dataset</c> it names the ways to get a
    /// smaller answer: the dataset-level members plus the first requested column do not fit.
    /// </summary>
    public static string NoticeNote(string toolName, string? argumentsJson)
    {
        if (!string.Equals(toolName, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase))
        {
            return "Result exceeded token budget. Use pagination (offset/limit) or filters to narrow the result.";
        }

        var column = TryReadColumnCursor(argumentsJson, out var columnOffset, out var namedColumns) && !namedColumns
            ? $"the column at columnOffset {columnOffset}"
            : "the first requested column";
        return $"Result exceeded token budget even with only the dataset-level members and {column}. Request columns by name with "
            + "columns, leave longDescription out of include, select fewer sections with include, use brief: true, or ask for a larger "
            + "Mcp:MaxTokensPerCall.";
    }

    private static string? ShortenDatasets(JsonObject root, bool isList, int maxChars, int maxTokens)
    {
        if (root[DatasetsProperty] is not JsonArray datasets)
        {
            return null;
        }

        var offset = root["offset"] is JsonValue offsetValue && offsetValue.TryGetValue<int>(out var o) ? o : 0;
        var note = isList
            ? "Result shortened to fit the token budget. Use offset and limit to read the remaining datasets."
            : "Result shortened to fit the token budget. Use a smaller limit or a more specific query.";
        return KeepPrefix(root, DatasetsProperty, ItemsOf(datasets), maxChars, (copy, keep, omitted) =>
        {
            if (isList)
            {
                copy["truncated"] = true;
                copy["nextOffset"] = offset + keep;
            }

            copy["_meta"] = Meta(maxTokens, keep, omitted, nextColumnOffset: null, note);
        });
    }

    /// <summary>
    /// Issue #820: keeps every dataset-level member (keys, indexes, required filters, relations, access status) and the longest column
    /// prefix. The cursor continues the caller's own <c>columnOffset</c>; a <c>columns</c> name selection wins over paging, so it gets
    /// no offset cursor.
    /// </summary>
    private static string? ShortenColumns(JsonObject root, string? argumentsJson, int maxChars, int maxTokens)
    {
        if (root[ColumnsProperty] is not JsonArray { Count: > 0 } columns || !TryReadColumnCursor(argumentsJson, out var columnOffset, out var namedColumns))
        {
            return null;
        }

        // The caller's visible column count: set by the #704 narrowing, otherwise the answer carries every visible column.
        var columnTotal = root[ColumnTotalProperty] is JsonValue totalValue && totalValue.TryGetValue<int>(out var total) ? total : columns.Count;
        var brief = root["view"] is JsonValue view && view.TryGetValue<string>(out var viewName)
            && string.Equals(viewName, McpDatasetCatalog.BriefView, StringComparison.Ordinal);
        var note = namedColumns
            ? "Columns shortened to fit the token budget. Call describe_dataset again and name the columns not returned in columns."
            : "Columns shortened to fit the token budget. Call describe_dataset again with the same arguments and columnOffset set to "
              + "_meta.nextColumnOffset to read the remaining columns (columnTotal minus nextColumnOffset remain; omitted counts only "
              + "the columns cut from this answer), or name the wanted columns in columns."
              + (brief ? string.Empty : " brief: true returns smaller column signatures.");
        return KeepPrefix(root, ColumnsProperty, ItemsOf(columns), maxChars, (copy, keep, omitted) =>
        {
            copy[ColumnTotalProperty] = columnTotal;
            copy["_meta"] = Meta(maxTokens, keep, omitted, namedColumns ? null : columnOffset + keep, note);
        });
    }

    /// <summary>
    /// Binary search over the number of kept items; each candidate is <paramref name="root"/> with a prefix of <paramref name="items"/> in
    /// <paramref name="property"/>, marked by <paramref name="mark"/>. Null when not even the first item fits.
    /// </summary>
    private static string? KeepPrefix(JsonObject root, string property, JsonNode?[] items, int maxChars, Action<JsonObject, int, int> mark)
    {
        // The envelope keeps the member in place but empty, so a candidate clones only the envelope and its kept items.
        root[property] = new JsonArray();
        var low = 1;
        var high = items.Length;
        string? best = null;
        while (low <= high)
        {
            var keep = low + ((high - low) / 2);
            var candidate = Build(root, property, items, keep, mark);
            if (candidate.Length <= maxChars)
            {
                best = candidate;
                low = keep + 1;
            }
            else
            {
                high = keep - 1;
            }
        }

        return best;
    }

    private static string Build(JsonObject root, string property, JsonNode?[] items, int keep, Action<JsonObject, int, int> mark)
    {
        // Work on a copy so the next candidate starts from the untouched envelope.
        var copy = (JsonObject)root.DeepClone();
        var kept = new JsonArray();
        for (var i = 0; i < keep; i++)
        {
            kept.Add(items[i]?.DeepClone());
        }

        copy[property] = kept;
        mark(copy, keep, items.Length - keep);
        return copy.ToJsonString(WriteOptions);
    }

    private static JsonObject Meta(int maxTokens, int returned, int omitted, int? nextColumnOffset, string note)
    {
        var meta = new JsonObject
        {
            ["truncated"] = true,
            ["reason"] = "TOKEN_BUDGET_EXCEEDED",
            ["maxTokens"] = maxTokens,
            ["returned"] = returned,
            ["omitted"] = omitted
        };
        if (nextColumnOffset is { } next)
        {
            meta["nextColumnOffset"] = next;
        }

        meta["note"] = note;
        return meta;
    }

    private static JsonNode?[] ItemsOf(JsonArray array)
    {
        var items = new JsonNode?[array.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = array[i];
        }

        return items;
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>columnOffset</c> (default 0) and whether a <c>columns</c> name selection was given; false when unreadable.</summary>
    private static bool TryReadColumnCursor(string? argumentsJson, out int columnOffset, out bool namedColumns)
    {
        columnOffset = 0;
        namedColumns = false;
        if (argumentsJson is null || ParseObject(argumentsJson) is not { } arguments)
        {
            return false;
        }

        namedColumns = arguments[ColumnsProperty] is not null;
        return arguments["columnOffset"] is not { } offset
            || (offset is JsonValue value && value.TryGetValue(out columnOffset) && columnOffset >= 0);
    }
}
