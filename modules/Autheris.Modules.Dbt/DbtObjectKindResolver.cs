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
    internal const string ColumnRoleMetaKey = "autheris.column_role";
    internal const string CountMeasureMetaKey = "autheris.count_measure";
    private const int MaxRawLength = 16;
    private const int MaxEchoLength = 64;
    private const int MaxMetaJsonLength = 4096;

    internal static bool TryResolveForIngestion(
        DbtModelDefinition model,
        ICollection<string>? warnings,
        out DiscoveryObjectKind kind,
        out string? countMeasure)
    {
        ArgumentNullException.ThrowIfNull(model);
        countMeasure = null;

        var raw = ReadMeta(model.Meta, MetaKey, "object_kind");
        var isSource = string.Equals(model.Materialization, "source", StringComparison.OrdinalIgnoreCase);

        if (!isSource)
        {
            if (raw is not null && raw.AsSpan().Trim().Equals("metric_view", StringComparison.OrdinalIgnoreCase))
            {
                AddWarning(warnings, $"Model '{DbtIdentifierSanitizer.ForLog(model.Name)}': {MetaKey} 'metric_view' is only permitted on dbt sources.");
                kind = DiscoveryObjectKind.Table;
                return false;
            }

            kind = string.Equals(model.Materialization, "view", StringComparison.OrdinalIgnoreCase)
                ? DiscoveryObjectKind.View
                : DiscoveryObjectKind.Table;
            return true;
        }

        kind = raw is null ? DiscoveryObjectKind.Table : Parse(raw, model.Name, warnings);
        if (kind == DiscoveryObjectKind.MetricView)
        {
            var rawCountMeasure = ReadMeta(model.Meta, CountMeasureMetaKey, "count_measure");
            if (!string.IsNullOrWhiteSpace(rawCountMeasure))
            {
                countMeasure = rawCountMeasure.Trim();
            }
        }

        return true;
    }

    internal static bool TryResolveColumnRole(
        DbtColumnDefinition column,
        DiscoveryObjectKind tableKind,
        string tableName,
        ICollection<string>? warnings,
        out MetricViewColumnRole? role)
    {
        ArgumentNullException.ThrowIfNull(column);
        role = null;
        var raw = ReadMeta(column.Meta, ColumnRoleMetaKey, "column_role");
        if (raw is null)
        {
            return true;
        }

        if (tableKind != DiscoveryObjectKind.MetricView)
        {
            AddWarning(warnings, $"Table '{DbtIdentifierSanitizer.ForLog(tableName)}': column '{DbtIdentifierSanitizer.ForLog(column.Name)}' declares {ColumnRoleMetaKey}, which is only permitted on metric_view objects.");
            return false;
        }

        var trimmed = raw.AsSpan().Trim();
        if (trimmed.Equals("dimension", StringComparison.OrdinalIgnoreCase))
        {
            role = MetricViewColumnRole.Dimension;
            return true;
        }

        if (trimmed.Equals("measure", StringComparison.OrdinalIgnoreCase))
        {
            role = MetricViewColumnRole.Measure;
            return true;
        }

        AddWarning(warnings, $"Metric view '{DbtIdentifierSanitizer.ForLog(tableName)}': column '{DbtIdentifierSanitizer.ForLog(column.Name)}' has unknown {ColumnRoleMetaKey} '{DbtIdentifierSanitizer.ForLog(raw.Length > MaxEchoLength ? raw[..MaxEchoLength] : raw)}', left unexposed.");
        return true;
    }

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

        var raw = ReadMeta(model.Meta, MetaKey, "object_kind");
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

            if (trimmed.Equals("metric_view", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoveryObjectKind.MetricView;
            }

            if (trimmed.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                return DiscoveryObjectKind.Table;
            }
        }

        AddWarning(warnings, $"Table '{DbtIdentifierSanitizer.ForLog(name)}': unknown {MetaKey} '{DbtIdentifierSanitizer.ForLog(raw.Length > MaxEchoLength ? raw[..MaxEchoLength] : raw)}', registered as table.");
        return DiscoveryObjectKind.Table;
    }

    private static void AddWarning(ICollection<string>? warnings, string message)
    {
        if (warnings is not null && !warnings.Contains(message))
        {
            warnings.Add(message);
        }
    }

    private static string? ReadMeta(IReadOnlyDictionary<string, string> meta, string flatKey, string jsonProperty)
    {
        if (meta.TryGetValue(flatKey, out var flat))
        {
            return flat;
        }

        if (meta.TryGetValue("autheris", out var json) && !string.IsNullOrWhiteSpace(json) && json.Length <= MaxMetaJsonLength)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(jsonProperty, out var prop))
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

    internal static bool TryResolveColumnRoles(
        DbtModelDefinition model,
        DiscoveryObjectKind tableKind,
        ICollection<string>? warnings,
        out Dictionary<string, MetricViewColumnRole?> resolvedRoles)
    {
        resolvedRoles = new Dictionary<string, MetricViewColumnRole?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, col) in model.Columns)
        {
            if (!TryResolveColumnRole(col, tableKind, model.Name, warnings, out var role))
            {
                return false;
            }

            resolvedRoles[col.Name] = role;
        }

        return true;
    }

    internal static bool HasExplicitColumnRole(DbtColumnDefinition column) =>
        ReadMeta(column.Meta, ColumnRoleMetaKey, "column_role") is not null;
}
