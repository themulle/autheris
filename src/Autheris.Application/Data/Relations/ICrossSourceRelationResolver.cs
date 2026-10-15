namespace Autheris.Application.Data.Relations;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Abstraction for fetching raw rows from a target data source by join key batch or filter (#75).
/// Governance (RLS, mandatory virtual filters, and column masking) is enforced by <see cref="ICrossSourceRelationResolver"/>.
/// </summary>
public interface ICrossSourceRowReader
{
    /// <summary>
    /// Reads rows from <paramref name="targetTable"/> where <paramref name="joinKeyColumns"/> match any key in <paramref name="batchKeys"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        TableMetadata targetTable,
        IReadOnlyList<string> joinKeyColumns,
        IReadOnlyList<CompositeKey> batchKeys,
        CancellationToken ct = default);

    /// <summary>
    /// Reads candidate rows from <paramref name="targetTable"/> for a cross-source filter pre-query.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsForFilterAsync(
        TableMetadata targetTable,
        string filterSql,
        int maxRows,
        CancellationToken ct = default);
}

/// <summary>
/// Issue #75: Resolves explicit cross-data-source <see cref="TableRelation"/> navigations and bounded filter pushdowns
/// with full per-source governance, key-clearance checks, parameter-bounded batching, and dedicated audit logging.
/// </summary>
public interface ICrossSourceRelationResolver
{
    /// <summary>
    /// Returns true when the parent and child tables belong to different data sources (different source name, data source type, or domain).
    /// </summary>
    bool IsCrossDataSource(TableMetadata parentTable, TableMetadata childTable);

    /// <summary>
    /// Validates that a query does not sort the root table by a column on a cross-source relation.
    /// Throws <see cref="CrossSourceRelationException"/> with <see cref="CrossSourceRelationErrorCodes.CrossSourceSortNotSupported"/> when violated.
    /// </summary>
    void ValidateNoCrossSourceSort(
        TableRelation relation,
        TableMetadata parentTable,
        TableMetadata childTable,
        IReadOnlyList<string>? sortByTargetColumns);

    /// <summary>
    /// Resolves a <c>ManyToOne</c> or <c>OneToMany</c> cross-source relation for a page of parent rows via parameter-bounded batched key lookup.
    /// </summary>
    Task<CrossSourceRelationResult> ResolveRelationAsync(
        CrossSourceRelationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a filter on the target data source first and returns the bounded set of matching join keys to push into the parent query.
    /// Fails closed with <see cref="CrossSourceRelationErrorCodes.CrossSourceFilterKeySetExceeded"/> when more than <c>MaxCrossSourceFilterKeys</c> match.
    /// </summary>
    Task<CrossSourceFilterKeySetResult> ResolveCrossSourceFilterKeysAsync(
        CrossSourceFilterPushdownRequest request,
        CancellationToken ct = default);
}
