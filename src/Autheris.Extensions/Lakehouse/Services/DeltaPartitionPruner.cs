namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// P11: Evaluates partition equality and min/max bound predicates against Delta data files.
/// </summary>
public sealed class DeltaPartitionPruner : IDeltaPartitionPruner
{
    private readonly ILogger<DeltaPartitionPruner> _logger;

    public DeltaPartitionPruner(ILogger<DeltaPartitionPruner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<DeltaDataFile> PruneDataFiles(
        IReadOnlyList<DeltaDataFile> allFiles,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyDictionary<string, string> predicates)
        => PruneDataFiles(allFiles, partitionColumns, predicates, Array.Empty<string>());

    public IReadOnlyList<DeltaDataFile> PruneDataFiles(
        IReadOnlyList<DeltaDataFile> allFiles,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyDictionary<string, string> predicates,
        IReadOnlyCollection<string> mandatoryColumns)
    {
        ArgumentNullException.ThrowIfNull(mandatoryColumns);
        if (allFiles.Count == 0 || (predicates.Count == 0 && mandatoryColumns.Count == 0))
        {
            return allFiles;
        }

        var matchingFiles = new List<DeltaDataFile>(allFiles.Count);

        foreach (var file in allFiles)
        {
            // SEC E-3: fail-closed for mandatory (tenant) columns - a file without ownership proof is dropped.
            if (!HasMandatoryEvidence(file, predicates, mandatoryColumns))
            {
                continue;
            }

            if (FileMatchesPredicates(file, partitionColumns, predicates))
            {
                matchingFiles.Add(file);
            }
        }

        _logger.LogDebug("Pruned Delta files from {Total} down to {Matching} files.", allFiles.Count, matchingFiles.Count);
        return matchingFiles;
    }

    private static bool HasMandatoryEvidence(
        DeltaDataFile file,
        IReadOnlyDictionary<string, string> predicates,
        IReadOnlyCollection<string> mandatoryColumns)
    {
        foreach (var column in mandatoryColumns)
        {
            if (!predicates.TryGetValue(column, out var expected) ||
                !LakehouseLocationGuard.ProvesTenantOwnership(
                    file.PartitionValues, file.MinValues, file.MaxValues, column,
                    LakehouseLocationGuard.NormalizeEqualityPredicate(expected)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FileMatchesPredicates(
        DeltaDataFile file,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyDictionary<string, string> predicates)
    {
        foreach (var (column, targetValue) in predicates)
        {
            // 1. Direct Partition Column Match
            if (file.PartitionValues.TryGetValue(column, out var partVal))
            {
                if (!string.Equals(partVal, targetValue, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // 2. Data Skipping via Min/Max bounds
            if (file.MinValues != null && file.MaxValues != null &&
                file.MinValues.TryGetValue(column, out var minStr) &&
                file.MaxValues.TryGetValue(column, out var maxStr))
            {
                if (double.TryParse(targetValue, out var targetNum) &&
                    double.TryParse(minStr, out var minNum) &&
                    double.TryParse(maxStr, out var maxNum))
                {
                    // If target value is outside [min, max] range for equality check
                    if (targetNum < minNum || targetNum > maxNum)
                    {
                        return false;
                    }
                }
                else
                {
                    // Lexicographic string comparison
                    if (string.CompareOrdinal(targetValue, minStr) < 0 ||
                        string.CompareOrdinal(targetValue, maxStr) > 0)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
