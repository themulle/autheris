namespace Autheris.Modules.Dbt;

using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// Reads the raw <c>meta.golden_queries</c> declaration of a dbt model or source node (<c>config.meta</c> wins over <c>meta</c>).
/// Reading is bounded: at most <see cref="DbtGoldenQueryMeta.MaxRetainedEntries"/> entries are retained and a single value larger than
/// <see cref="MaxRawValueLength"/> is dropped with a reason code. Validation and sanitising happen at import time.
/// </summary>
internal static class DbtGoldenQueryMetaReader
{
    internal const int MaxRawValueLength = 256 * 1024;

    public static DbtGoldenQueryMeta Read(JsonElement node, string uniqueId, RelationCollector warnings)
    {
        if (!TryGetDeclaration(node, out var declaration))
        {
            return DbtGoldenQueryMeta.None;
        }

        if (declaration.ValueKind == JsonValueKind.Null)
        {
            return DbtGoldenQueryMeta.Empty;
        }

        if (declaration.ValueKind != JsonValueKind.Array)
        {
            warnings.Warn($"Node '{uniqueId}' declared an invalid meta.golden_queries value (GOLDEN_QUERIES_INVALID_SHAPE); the declaration was skipped.");
            return new DbtGoldenQueryMeta(true, 0, [], HasDeclaration: true);
        }

        var total = declaration.GetArrayLength();
        var entries = new List<DbtGoldenQueryRawEntry>(System.Math.Min(total, DbtGoldenQueryMeta.MaxRetainedEntries));
        var index = 0;
        foreach (var item in declaration.EnumerateArray())
        {
            if (entries.Count >= DbtGoldenQueryMeta.MaxRetainedEntries)
            {
                break;
            }

            entries.Add(ReadEntry(index++, item));
        }

        return new DbtGoldenQueryMeta(false, total, entries);
    }

    private static DbtGoldenQueryRawEntry ReadEntry(int index, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new DbtGoldenQueryRawEntry(index, null, null, null, null, "ENTRY_INVALID_SHAPE");
        }

        string? reason = null;
        var title = ReadText(item, "title", ref reason);
        var description = ReadText(item, "description", ref reason);
        var query = ReadText(item, "query", ref reason);
        var variables = ReadVariables(item, ref reason);
        return new DbtGoldenQueryRawEntry(index, title, description, query, variables, reason);
    }

    private static string? ReadText(JsonElement owner, string property, ref string? reason)
    {
        if (!owner.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            reason ??= "ENTRY_INVALID_SHAPE";
            return null;
        }

        var text = value.GetString();
        if (text is { Length: > MaxRawValueLength })
        {
            reason ??= "ENTRY_VALUE_TOO_LARGE";
            return null;
        }

        return text;
    }

    private static string? ReadVariables(JsonElement owner, ref string? reason)
    {
        if (!owner.TryGetProperty("variables", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return ReadText(owner, "variables", ref reason);
        }

        var raw = value.GetRawText();
        if (raw.Length > MaxRawValueLength)
        {
            reason ??= "ENTRY_VALUE_TOO_LARGE";
            return null;
        }

        return raw;
    }

    private static bool TryGetDeclaration(JsonElement node, out JsonElement declaration)
    {
        if (node.TryGetProperty("config", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("meta", out var cfgMeta) &&
            cfgMeta.ValueKind == JsonValueKind.Object &&
            cfgMeta.TryGetProperty("golden_queries", out declaration))
        {
            return true;
        }

        if (node.TryGetProperty("meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("golden_queries", out declaration))
        {
            return true;
        }

        declaration = default;
        return false;
    }
}
