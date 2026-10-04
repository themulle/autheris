namespace Autheris.Extensions.Lakehouse.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// P11: Evaluates partition filters and data skipping min/max statistics against Delta data files.
/// </summary>
public interface IDeltaPartitionPruner
{
    /// <summary>
    /// Prunes Delta data files using partition values and column min/max bounds.
    /// </summary>
    IReadOnlyList<DeltaDataFile> PruneDataFiles(
        IReadOnlyList<DeltaDataFile> allFiles,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyDictionary<string, string> predicates);
}
