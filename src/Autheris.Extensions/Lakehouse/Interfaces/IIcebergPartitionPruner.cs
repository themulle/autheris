namespace Autheris.Extensions.Lakehouse.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Evaluates query filter predicates against Iceberg partition values and column min/max bounds
/// to prune non-matching data files prior to storage I/O.
/// </summary>
public interface IIcebergPartitionPruner
{
    /// <summary>
    /// Prunes the input list of data files based on query filters and partition specifications.
    /// </summary>
    /// <param name="allFiles">All candidate data files from the current snapshot.</param>
    /// <param name="filterPredicates">Field filter predicates from the GraphQL query.</param>
    /// <param name="partitionSpec">Iceberg partition specification.</param>
    /// <returns>Filtered list of data files that match the query predicates.</returns>
    IReadOnlyList<IcebergDataFile> PruneDataFiles(
        IReadOnlyList<IcebergDataFile> allFiles,
        IReadOnlyDictionary<string, string> filterPredicates,
        IcebergPartitionSpec partitionSpec);

    /// <summary>
    /// Prunes data files; files lacking a partition value or min/max statistics for any of the
    /// <paramref name="mandatoryColumns"/> (e.g. the tenant column) are discarded (fail-closed).
    /// </summary>
    IReadOnlyList<IcebergDataFile> PruneDataFiles(
        IReadOnlyList<IcebergDataFile> allFiles,
        IReadOnlyDictionary<string, string> filterPredicates,
        IcebergPartitionSpec partitionSpec,
        IReadOnlyCollection<string> mandatoryColumns);
}
