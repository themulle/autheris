namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>
/// Resolves the descriptive object kind (table or view) of a dbt manifest entry. The kind is a closed set: a model takes
/// <c>View</c> only from <c>materialized: view</c>; a source (or any entry without a model) takes the <c>autheris.object_kind</c>
/// meta key. Any other value becomes <c>Table</c> with a sanitized warning and never fails the sync.
/// </summary>
internal static class DbtObjectKindResolver
{
    internal const string MetaKey = "autheris.object_kind";
    private const int MaxRawLength = 16;
    private const int MaxEchoLength = 64;
    private const int MaxMetaJsonLength = 4096;

    internal static DiscoveryObjectKind Resolve(DbtModelDefinition model, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.Equals(model.Materialization, "view", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.View;
        }

        if (!string.Equals(model.Materialization, "source", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryObjectKind.Table;
        }

        var raw = ReadMeta(model.Meta);
        if (raw is null)
        {
            return DiscoveryObjectKind.Table;
        }

        return Parse(raw, model.Name, warnings);
    }

    internal static DiscoveryObjectKind Parse(string raw, string name, ICollection<string>? warnings)
    {
        if (raw.Length <= MaxRawLength)
        {
            var trimmed = raw.AsSpan().Trim();
            if (trimmed.Equals("view", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoveryObjectKind.View;
            }

            if (trimmed.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoveryObjectKind.Table;
            }
        }

        var message = $"Table '{DbtIdentifierSanitizer.ForLog(name)}': unknown {MetaKey} '{DbtIdentifierSanitizer.ForLog(raw.Length > MaxEchoLength ? raw[..MaxEchoLength] : raw)}', registered as table.";
        if (warnings is not null && !warnings.Contains(message))
        {
            warnings.Add(message);
        }

        return DiscoveryObjectKind.Table;
    }

    private static string? ReadMeta(IReadOnlyDictionary<string, string> meta)
    {
        if (meta.TryGetValue(MetaKey, out var flat))
        {
            return flat;
        }

        if (meta.TryGetValue("autheris", out var json) && !string.IsNullOrWhiteSpace(json) && json.Length <= MaxMetaJsonLength)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("object_kind", out var prop))
                {
                    return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}
