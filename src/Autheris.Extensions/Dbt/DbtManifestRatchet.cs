namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Application.DataCatalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// EXT-05: Manifest ratchet semantics. The dbt manifest is an external mirror of model metadata,
/// so security-relevant properties can only be tightened and never relaxed.
/// </summary>
internal static class DbtManifestRatchet
{
    private static readonly HashSet<string> ProtectedMetaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pii", "is_pii", "sensitivity", "release_value_list", "release_allowed_values", "classification_review"
    };

    public static ImmutableArray<string> MergeRequiredFilterColumns(
        TableIdentifier tableId,
        ImmutableArray<string> stored,
        ImmutableArray<string>? incoming,
        bool incomingSpecified,
        List<DbtSuppressedRelaxation> suppressedRelaxations,
        ILogger? logger)
    {
        if (!incomingSpecified || incoming == null)
        {
            return stored.IsDefault ? ImmutableArray<string>.Empty : stored;
        }

        var union = CatalogGovernanceRatchet.UnionRequiredFilterColumns(stored, incoming.Value);
        var s = stored.IsDefault ? ImmutableArray<string>.Empty : stored;
        var inc = incoming.Value.IsDefault ? ImmutableArray<string>.Empty : incoming.Value;

        if (s.Length > 0 && (inc.Length < s.Length || s.Any(col => !inc.Contains(col, StringComparer.OrdinalIgnoreCase))))
        {
            logger?.LogWarning("Table {Table}: manifest sync attempted to relax RequiredFilterColumns; stored filters preserved.", tableId);
            suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                tableId,
                "required_filter_columns",
                "Stored required filter columns cannot be removed or relaxed via manifest sync."));
        }

        return union;
    }

    public static ImmutableArray<ImmutableArray<string>> MergeRequiredFilterColumnGroups(
        TableIdentifier tableId,
        ImmutableArray<ImmutableArray<string>> stored,
        ImmutableArray<ImmutableArray<string>>? incoming,
        bool incomingSpecified,
        List<DbtSuppressedRelaxation> suppressedRelaxations,
        ILogger? logger)
    {
        if (!incomingSpecified || incoming == null)
        {
            return stored.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : stored;
        }

        var union = CatalogGovernanceRatchet.UnionRequiredFilterColumnGroups(stored, incoming.Value);
        var s = stored.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : stored;
        var inc = incoming.Value.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : incoming.Value;

        if (s.Length > 0 && inc.Length < s.Length)
        {
            logger?.LogWarning("Table {Table}: manifest sync attempted to relax RequiredFilterColumnGroups; stored groups preserved.", tableId);
            suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                tableId,
                "required_filter_column_groups",
                "Stored required filter column groups cannot be removed or relaxed via manifest sync."));
        }

        return union;
    }

    public static IReadOnlyDictionary<string, string> MergeColumnMeta(
        TableIdentifier tableId,
        string columnName,
        IReadOnlyDictionary<string, string> storedMeta,
        IReadOnlyDictionary<string, string>? incomingMeta,
        List<DbtSuppressedRelaxation> suppressedRelaxations,
        ILogger? logger)
    {
        var result = new Dictionary<string, string>(storedMeta ?? ImmutableDictionary<string, string>.Empty, StringComparer.OrdinalIgnoreCase);
        if (incomingMeta == null || incomingMeta.Count == 0)
        {
            // If incoming has no meta or dropped meta, check if stored had PII
            if (IsPii(storedMeta))
            {
                logger?.LogWarning("Table {Table}.{Column}: manifest sync omitted PII meta; stored PII preserved.", tableId, columnName);
                suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                    tableId,
                    $"{columnName}.pii",
                    "Stored PII flag cannot be dropped via manifest sync."));
            }
            return result;
        }

        // 1. Check PII dropping
        var storedIsPii = IsPii(storedMeta);
        var incomingIsPii = IsPii(incomingMeta);
        if (storedIsPii && !incomingIsPii)
        {
            logger?.LogWarning("Table {Table}.{Column}: manifest sync attempted to drop or weaken PII; stored PII preserved.", tableId, columnName);
            suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                tableId,
                $"{columnName}.pii",
                "Stored PII flag cannot be dropped or weakened via manifest sync."));
            // Ensure stored PII marker is kept
            if (storedMeta != null)
            {
                if (storedMeta.TryGetValue("pii", out var pVal)) result["pii"] = pVal;
                if (storedMeta.TryGetValue("is_pii", out var ipVal)) result["is_pii"] = ipVal;
            }
        }
        else if (!storedIsPii && incomingIsPii)
        {
            // Tightening PII: allowed!
            if (incomingMeta.TryGetValue("pii", out var pVal)) result["pii"] = pVal;
            if (incomingMeta.TryGetValue("is_pii", out var ipVal)) result["is_pii"] = ipVal;
        }

        // 2. Sensitivity
        var storedSens = storedMeta?.TryGetValue("sensitivity", out var ss) == true ? ss : null;
        var incSens = incomingMeta.TryGetValue("sensitivity", out var ins) ? ins : null;
        if (!string.IsNullOrWhiteSpace(incSens))
        {
            var storedRank = Table.SensitivityRank(storedSens);
            var incRank = Table.SensitivityRank(incSens);
            if (incRank > storedRank)
            {
                // Tightening sensitivity: allowed!
                result["sensitivity"] = incSens;
            }
            else if (incRank < storedRank)
            {
                // Relaxation: suppressed
                logger?.LogWarning("Table {Table}.{Column}: manifest sync attempted to lower sensitivity from '{Stored}' to '{Incoming}'; stored sensitivity preserved.",
                    tableId, columnName, storedSens, incSens);
                suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                    tableId,
                    $"{columnName}.sensitivity",
                    $"Sensitivity cannot be lowered from '{storedSens}' to '{incSens}' via manifest sync."));
                if (storedSens != null) result["sensitivity"] = storedSens;
            }
        }

        // 3. release_* keys may never be added or widened by the manifest
        foreach (var key in incomingMeta.Keys)
        {
            if (key.StartsWith("release_", StringComparison.OrdinalIgnoreCase))
            {
                string? storedVal = null;
                var storedHasKey = storedMeta != null && storedMeta.TryGetValue(key, out storedVal) && !string.IsNullOrWhiteSpace(storedVal);
                var incVal = incomingMeta[key];
                if (!storedHasKey)
                {
                    logger?.LogWarning("Table {Table}.{Column}: manifest sync attempted to add protected release key '{Key}'; suppressed.", tableId, columnName, key);
                    suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                        tableId,
                        $"{columnName}.{key}",
                        $"Protected key '{key}' cannot be added via manifest sync."));
                    result.Remove(key);
                }
                else if (string.Equals(storedVal, "false", StringComparison.OrdinalIgnoreCase) && string.Equals(incVal, "true", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogWarning("Table {Table}.{Column}: manifest sync attempted to widen protected release key '{Key}'; suppressed.", tableId, columnName, key);
                    suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                        tableId,
                        $"{columnName}.{key}",
                        $"Protected key '{key}' cannot be widened via manifest sync."));
                    result[key] = storedVal!;
                }
            }
        }

        // 4. classification_review
        if (storedMeta != null && storedMeta.TryGetValue("classification_review", out var storedReview))
        {
            if (incomingMeta.TryGetValue("classification_review", out var incReview) && !string.Equals(storedReview, incReview, StringComparison.Ordinal))
            {
                logger?.LogWarning("Table {Table}.{Column}: manifest sync attempted to modify classification_review; suppressed.", tableId, columnName);
                suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                    tableId,
                    $"{columnName}.classification_review",
                    "classification_review cannot be overwritten via manifest sync."));
            }
            result["classification_review"] = storedReview;
        }

        // 5. Overlay any other non-protected keys
        foreach (var (k, v) in incomingMeta)
        {
            if (!ProtectedMetaKeys.Contains(k) && !k.StartsWith("release_", StringComparison.OrdinalIgnoreCase))
            {
                result[k] = v;
            }
        }

        return result;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> MergeSyntheticSamples(
        TableIdentifier tableId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? stored,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? incoming,
        List<DbtSuppressedRelaxation> suppressedRelaxations,
        ILogger? logger)
    {
        if (stored != null && stored.Count > 0)
        {
            if (incoming != null && incoming.Count > 0)
            {
                logger?.LogWarning("Table {Table}: manifest sync attempted to replace PredefinedSyntheticSamples; stored samples preserved.", tableId);
                suppressedRelaxations.Add(new DbtSuppressedRelaxation(
                    tableId,
                    "predefined_synthetic_samples",
                    "Stored predefined synthetic samples cannot be replaced via manifest sync."));
            }
            return stored;
        }

        return incoming ?? Array.Empty<IReadOnlyDictionary<string, object?>>();
    }

    private static bool IsPii(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta == null) return false;
        if (meta.TryGetValue("pii", out var p) && (string.Equals(p, "true", StringComparison.OrdinalIgnoreCase) || p == "1"))
            return true;
        if (meta.TryGetValue("is_pii", out var ip) && (string.Equals(ip, "true", StringComparison.OrdinalIgnoreCase) || ip == "1"))
            return true;
        return false;
    }
}
