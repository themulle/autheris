namespace Autheris.Application.Mcp.Services;

using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>What <see cref="McpRowShortening.Shorten"/> did with a data result that is over the token budget.</summary>
internal enum McpRowShorteningStatus
{
    /// <summary>The result has no row array to shorten (or cannot be parsed): the caller keeps its budget error.</summary>
    NotApplicable = 0,

    /// <summary>The re-serialized result fits the budget as it is.</summary>
    Fits,

    /// <summary>The result was cut to the longest prefix of whole rows that fits.</summary>
    Shortened,

    /// <summary>Not even one whole row fits next to the other members.</summary>
    FirstRowTooLarge,
}

internal readonly record struct McpRowShorteningOutcome(McpRowShorteningStatus Status, string Json, int ReturnedRows);

/// <summary>
/// Shortens an over-budget <c>query_sql</c>, <c>query_dataset</c> or <c>sample_rows</c> result to the longest prefix of whole rows that
/// fits, instead of replacing the payload. Rows are copied as parsed JSON nodes, so nothing inside a row or cell is ever cut or
/// re-encoded. The caller runs it after masking, scrubbing and inspection.
/// </summary>
internal static class McpRowShortening
{
    /// <summary>Longest continuation token the codec accepts; a longer offer is ignored.</summary>
    private const int MaxCursorLength = 512;

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool AppliesTo(string toolName) => RowsProperty(toolName) is not null;

    /// <summary>The fixed note of the error that replaces a result whose first row alone exceeds the budget; no payload or argument text.</summary>
    public static string FirstRowNote(string toolName)
    {
        const string Head = "A single row exceeds the token budget. ";
        const string Tail = ", or ask for a larger Mcp:MaxTokensPerCall.";
        if (string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase))
        {
            return Head + "Use select to request fewer columns" + Tail;
        }

        if (string.Equals(toolName, McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase))
        {
            return Head + "Use query_dataset with select to request fewer columns" + Tail;
        }

        return Head + "Select fewer or shorter columns" + Tail;
    }

    /// <summary>
    /// Cuts <paramref name="json"/> to the longest prefix of whole rows whose payload fits <paramref name="maxChars"/>. <paramref name="resumeAfter"/>
    /// is read for <c>query_sql</c> only, is called only with a row count below the number of rows in the payload and at most twice.
    /// </summary>
    public static McpRowShorteningOutcome Shorten(string toolName, string json, int maxChars, Func<int, string?>? resumeAfter)
    {
        var rowsName = RowsProperty(toolName);
        if (rowsName is null || ParseObject(json) is not { } root || root[rowsName] is not JsonArray array || array.Count == 0)
        {
            return default;
        }

        var whole = root.ToJsonString(Relaxed);
        if (whole.Length <= maxChars)
        {
            return new McpRowShorteningOutcome(McpRowShorteningStatus.Fits, whole, array.Count);
        }

        var count = array.Count;
        var items = new JsonNode?[count];
        var lengths = new int[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = array[i];
            lengths[i] = items[i]?.ToJsonString(Relaxed).Length ?? 4;
        }

        array.Clear();
        var isSql = string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase);
        var resume = isSql ? resumeAfter : null;

        var kept = Fit(root, toolName, count - 1, lengths, null, maxChars);
        if (kept < 1)
        {
            return new McpRowShorteningOutcome(McpRowShorteningStatus.FirstRowTooLarge, string.Empty, 0);
        }

        string? cursor = null;
        if (resume is not null && resume(kept) is { Length: > 0 and <= MaxCursorLength } probe)
        {
            var withCursor = Fit(root, toolName, kept, lengths, probe, maxChars);
            if (withCursor < 1)
            {
                return new McpRowShorteningOutcome(McpRowShorteningStatus.FirstRowTooLarge, string.Empty, 0);
            }

            cursor = probe;
            if (withCursor != kept)
            {
                kept = withCursor;
                cursor = resume(kept) is { Length: > 0 and <= MaxCursorLength } exact ? exact : null;
            }
        }

        for (var i = 0; i < kept; i++)
        {
            array.Add(items[i]);
        }

        // The sizing used an approximate row count; without a minted cursor a digit of drift is absorbed by dropping rows (bounded).
        for (var attempt = 0; ; attempt++)
        {
            ApplyMarks(root, toolName, kept, cursor);
            var result = root.ToJsonString(Relaxed);
            if (result.Length <= maxChars)
            {
                return new McpRowShorteningOutcome(McpRowShorteningStatus.Shortened, result, kept);
            }

            if (cursor is not null || kept <= 1 || attempt >= 2)
            {
                return new McpRowShorteningOutcome(McpRowShorteningStatus.FirstRowTooLarge, string.Empty, 0);
            }

            array.RemoveAt(--kept);
        }
    }

    /// <summary>The largest row count up to <paramref name="upTo"/> whose payload fits, from prefix sums; 0 when not even one row fits.</summary>
    private static int Fit(JsonObject root, string toolName, int upTo, int[] lengths, string? cursor, int maxChars)
    {
        ApplyMarks(root, toolName, upTo, cursor);
        long total = root.ToJsonString(Relaxed).Length;
        var kept = 0;
        for (var i = 0; i < upTo; i++)
        {
            total += lengths[i] + (i > 0 ? 1 : 0);
            if (total > maxChars)
            {
                break;
            }

            kept = i + 1;
        }

        return kept;
    }

    private static void ApplyMarks(JsonObject root, string toolName, int kept, string? cursor)
    {
        if (string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase))
        {
            root["count"] = kept;
            root["hasMore"] = true;
            return;
        }

        root["rowCount"] = kept;
        if (!string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        root["hasMore"] = true;
        if (cursor is null)
        {
            root.Remove("next");
            root.Remove("cursor");
        }
        else
        {
            root["next"] = cursor;
            root["cursor"] = cursor;
        }
    }

    private static string? RowsProperty(string toolName)
    {
        if (string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase))
        {
            return "data";
        }

        return string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, McpDatasetTools.SampleRows, StringComparison.OrdinalIgnoreCase)
            ? "rows"
            : null;
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
}
