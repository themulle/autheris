namespace Autheris.Application.Mcp.Services;

using System;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Shortens an over-budget catalog result at structure boundaries (whole datasets) instead of replacing it with a notice.
/// Only parameters that the tools really have are named in the note: <c>offset</c>/<c>limit</c> for <c>list_datasets</c>
/// and <c>limit</c> for <c>search_catalog</c>.
/// </summary>
internal static class McpBudgetShortening
{
    private const string DatasetsProperty = "datasets";

    public static bool AppliesTo(string toolName) =>
        string.Equals(toolName, McpDatasetTools.ListDatasets, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the longest prefix of the <c>datasets</c> array whose serialization fits <paramref name="maxChars"/>.
    /// Returns false when the result has no such array or not even an empty array fits.
    /// </summary>
    public static bool TryShorten(string toolName, string json, int maxChars, int maxTokens, out string shortened)
    {
        shortened = json;
        if (!AppliesTo(toolName))
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is null || root[DatasetsProperty] is not JsonArray datasets)
        {
            return false;
        }

        var items = new JsonNode?[datasets.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = datasets[i];
        }

        var isList = string.Equals(toolName, McpDatasetTools.ListDatasets, StringComparison.OrdinalIgnoreCase);
        var offset = root["offset"] is JsonValue offsetValue && offsetValue.TryGetValue<int>(out var o) ? o : 0;

        // Binary search over the number of kept datasets; the candidate is rebuilt from the original items each time.
        var low = 0;
        var high = items.Length - 1;
        string? best = null;
        while (low <= high)
        {
            var keep = low + ((high - low) / 2);
            var candidate = Build(root, items, keep, isList, offset, maxTokens);
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

        if (best is null)
        {
            var empty = Build(root, items, 0, isList, offset, maxTokens);
            if (empty.Length > maxChars)
            {
                return false;
            }

            best = empty;
        }

        shortened = best;
        return true;
    }

    private static string Build(JsonObject original, JsonNode?[] items, int keep, bool isList, int offset, int maxTokens)
    {
        // Work on a copy so the next candidate starts from the untouched result.
        var copy = (JsonObject)original.DeepClone();
        var kept = new JsonArray();
        for (var i = 0; i < keep; i++)
        {
            kept.Add(items[i]?.DeepClone());
        }

        copy[DatasetsProperty] = kept;
        var omitted = items.Length - keep;
        if (isList)
        {
            copy["truncated"] = true;
            copy["nextOffset"] = offset + keep;
        }

        copy["_meta"] = new JsonObject
        {
            ["truncated"] = true,
            ["reason"] = "TOKEN_BUDGET_EXCEEDED",
            ["maxTokens"] = maxTokens,
            ["returned"] = keep,
            ["omitted"] = omitted,
            ["note"] = isList
                ? "Result shortened to fit the token budget. Use offset and limit to read the remaining datasets."
                : "Result shortened to fit the token budget. Use a smaller limit or a more specific query."
        };

        return copy.ToJsonString();
    }
}
