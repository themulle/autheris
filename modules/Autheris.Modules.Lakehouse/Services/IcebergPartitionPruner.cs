namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Evaluates query filter predicates against Iceberg partition values and column min/max bounds
/// to prune non-matching data files prior to storage I/O.
/// </summary>
public sealed class IcebergPartitionPruner : IIcebergPartitionPruner
{
    private readonly ILogger<IcebergPartitionPruner> _logger;

    public IcebergPartitionPruner(ILogger<IcebergPartitionPruner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<IcebergDataFile> PruneDataFiles(
        IReadOnlyList<IcebergDataFile> allFiles,
        IReadOnlyDictionary<string, string> filterPredicates,
        IcebergPartitionSpec partitionSpec)
    {
        return PruneDataFiles(allFiles, filterPredicates, partitionSpec, Array.Empty<string>());
    }

    public IReadOnlyList<IcebergDataFile> PruneDataFiles(
        IReadOnlyList<IcebergDataFile> allFiles,
        IReadOnlyDictionary<string, string> filterPredicates,
        IcebergPartitionSpec partitionSpec,
        IReadOnlyCollection<string> mandatoryColumns)
    {
        ArgumentNullException.ThrowIfNull(allFiles);
        ArgumentNullException.ThrowIfNull(filterPredicates);
        ArgumentNullException.ThrowIfNull(mandatoryColumns);

        if (allFiles.Count == 0 || (filterPredicates.Count == 0 && mandatoryColumns.Count == 0))
        {
            return allFiles;
        }

        var matchingFiles = new List<IcebergDataFile>(allFiles.Count);

        foreach (var file in allFiles)
        {
            // SEC M-35: fail-closed for mandatory (e.g. tenant) filters – files without partition value or statistics are dropped.
            if (!HasMandatoryEvidence(file, filterPredicates, mandatoryColumns))
            {
                continue;
            }

            if (FileMatchesFilters(file, filterPredicates))
            {
                matchingFiles.Add(file);
            }
        }

        var prunedCount = allFiles.Count - matchingFiles.Count;
        var efficiency = allFiles.Count > 0 ? (double)prunedCount / allFiles.Count * 100.0 : 0.0;

        _logger.LogInformation("Partition Pruning complete: Scanned {Total}, Kept {Kept}, Pruned {Pruned} ({Efficiency:F1}% skipped).",
            allFiles.Count, matchingFiles.Count, prunedCount, efficiency);

        return matchingFiles;
    }

    private static bool HasMandatoryEvidence(
        IcebergDataFile file,
        IReadOnlyDictionary<string, string> predicates,
        IReadOnlyCollection<string> mandatoryColumns)
    {
        foreach (var column in mandatoryColumns)
        {
            if (!predicates.ContainsKey(column))
            {
                // A mandatory filter without a predicate cannot be evaluated -> fail closed.
                return false;
            }

            // SEC E-3: only partition equality or lower == upper == value counts as ownership proof; overlapping bounds do not.
            if (!LakehouseLocationGuard.ProvesTenantOwnership(
                    file.PartitionValues, file.LowerBounds, file.UpperBounds, column,
                    LakehouseLocationGuard.NormalizeEqualityPredicate(predicates[column])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FileMatchesFilters(
        IcebergDataFile file,
        IReadOnlyDictionary<string, string> predicates)
    {
        foreach (var (column, filterVal) in predicates)
        {
            // 1. Check exact partition match
            if (file.PartitionValues.TryGetValue(column, out var partVal))
            {
                if (!EvaluatePredicate(partVal, filterVal))
                {
                    return false;
                }
            }

            // 2. Check Min/Max Upper & Lower Bounds
            if (file.LowerBounds != null && file.UpperBounds != null)
            {
                if (file.LowerBounds.TryGetValue(column, out var lower) &&
                    file.UpperBounds.TryGetValue(column, out var upper))
                {
                    if (!BoundsOverlap(lower, upper, filterVal))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool EvaluatePredicate(string actualVal, string filterExpr)
    {
        // Support comparison operators: '>= 2026-06-01', '> 100', '<= 50', '== value'
        var trimmed = filterExpr.Trim();

        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
        {
            var target = trimmed[2..].Trim();
            return string.Compare(actualVal, target, StringComparison.Ordinal) >= 0;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var target = trimmed[1..].Trim();
            return string.Compare(actualVal, target, StringComparison.Ordinal) > 0;
        }

        if (trimmed.StartsWith("<=", StringComparison.Ordinal))
        {
            var target = trimmed[2..].Trim();
            return string.Compare(actualVal, target, StringComparison.Ordinal) <= 0;
        }

        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            var target = trimmed[1..].Trim();
            return string.Compare(actualVal, target, StringComparison.Ordinal) < 0;
        }

        if (trimmed.StartsWith("==", StringComparison.Ordinal))
        {
            var target = trimmed[2..].Trim();
            return string.Equals(actualVal, target, StringComparison.Ordinal);
        }

        // Default: exact match
        return string.Equals(actualVal, trimmed, StringComparison.Ordinal);
    }

    private static bool BoundsOverlap(string lower, string upper, string filterExpr)
    {
        var trimmed = filterExpr.Trim();

        if (trimmed.StartsWith(">=", StringComparison.Ordinal) || trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            var target = trimmed.TrimStart('>', '=').Trim();
            // If the highest value in this file is strictly smaller than the target, skip
            if (string.Compare(upper, target, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }
        else if (trimmed.StartsWith("<=", StringComparison.Ordinal) || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            var target = trimmed.TrimStart('<', '=').Trim();
            // If the lowest value in this file is strictly greater than the target, skip
            if (string.Compare(lower, target, StringComparison.Ordinal) > 0)
            {
                return false;
            }
        }
        else
        {
            var target = trimmed.StartsWith("==", StringComparison.Ordinal) ? trimmed[2..].Trim() : trimmed;
            // Exact value must fall within [lower, upper]
            if (string.Compare(target, lower, StringComparison.Ordinal) < 0 ||
                string.Compare(target, upper, StringComparison.Ordinal) > 0)
            {
                return false;
            }
        }

        return true;
    }
}
