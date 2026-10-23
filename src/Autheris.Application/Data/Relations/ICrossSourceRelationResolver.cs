namespace Autheris.Application.Data.Relations;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Abstraction for fetching raw rows from a target data source by join key batch (#75).
/// Governance (RLS, column masking) is enforced by <see cref="ICrossSourceRelationResolver"/>; the reader itself never takes client text
/// (VF-04): it receives catalog-checked column names and key values only.
/// </summary>
public interface ICrossSourceRowReader
{
    /// <summary>Reads the rows of <see cref="CrossSourceKeyReadRequest.Target"/> whose join key matches any requested key.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        CrossSourceKeyReadRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Issue #75: Resolves explicit cross-data-source <see cref="TableRelation"/> navigations
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
}
