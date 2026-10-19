namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #75: Stable error codes for governed cross-data-source relation resolution.
/// </summary>
public static class CrossSourceRelationErrorCodes
{
    public const string CrossSourceRelationNotEnabled = "CROSS_SOURCE_RELATION_NOT_ENABLED";
    public const string CrossSourceRelationKeyNotClear = "CROSS_SOURCE_RELATION_KEY_NOT_CLEAR";
    public const string RelationKeyMasked = "RELATION_KEY_MASKED";
    public const string CrossSourceKeyMasked = "CROSS_SOURCE_KEY_MASKED";
    public const string CrossSourceFilterKeySetExceeded = "CROSS_SOURCE_FILTER_KEY_SET_EXCEEDED";
    public const string CrossSourceSortNotSupported = "CROSS_SOURCE_SORT_NOT_SUPPORTED";
}

/// <summary>
/// Issue #75: Exception raised when a cross-source relation violates governance or execution bounds.
/// </summary>
public sealed class CrossSourceRelationException : GatewaySecurityException
{
    public CrossSourceRelationException(string errorCode, string message, Exception? innerException = null)
        : base(message, errorCode, innerException)
    {
    }
}

/// <summary>
/// Options controlling batched key lookup and filter pushdown bounds across data sources (#75).
/// </summary>
public sealed record CrossSourceRelationOptions
{
    /// <summary>Maximum number of distinct join keys per <c>WHERE key IN (...)</c> batch query on the target source.</summary>
    public int MaxBatchSize { get; init; } = 500;

    /// <summary>Maximum number of distinct join keys returned by a cross-source filter pre-query before failing closed.</summary>
    public int MaxCrossSourceFilterKeys { get; init; } = 1000;

    /// <summary>Maximum total nested rows materialized across all batches in a single relation resolution.</summary>
    public int MaxNestedRows { get; init; } = 5000;

    /// <summary>Cost weight charged per executed target batch query.</summary>
    public double CostPerBatch { get; init; } = 2.0;

    /// <summary>Cost weight charged per materialized target row.</summary>
    public double CostPerRow { get; init; } = 0.05;
}

/// <summary>
/// Request to resolve a <see cref="TableRelation"/> across data sources for a page of parent rows (#75).
/// </summary>
public sealed record CrossSourceRelationRequest(
    TableRelation Relation,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> ParentRows,
    ClaimsPrincipal User,
    TenantId TenantId,
    IReadOnlyList<string>? SelectColumns = null,
    string? TargetFilterSql = null,
    IReadOnlyList<string>? SortByTargetColumns = null,
    int? PerParentLimit = null,
    string? ClientIp = null,
    TableAccessDecision? ParentDecision = null);

/// <summary>
/// Result of resolving a cross-source relation for a page of parent rows (#75).
/// </summary>
public sealed record CrossSourceRelationResult(
    TableRelation Relation,
    IReadOnlyList<IReadOnlyDictionary<string, object?>?> ManyToOneByParentIndex,
    IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>> OneToManyByParentIndex,
    int BatchesExecuted,
    int TargetRowsMaterialized,
    double QueryCost);

/// <summary>
/// Request to resolve matching join keys on a target data source for bounded filter pushdown into the parent source (#75).
/// </summary>
public sealed record CrossSourceFilterPushdownRequest(
    TableRelation Relation,
    string TargetFilterSql,
    IReadOnlyList<string> ReferencedTargetColumns,
    ClaimsPrincipal User,
    TenantId TenantId,
    string? ClientIp = null,
    TableAccessDecision? ParentDecision = null);

/// <summary>
/// Result of a bounded cross-source filter key resolution (#75).
/// </summary>
public sealed record CrossSourceFilterKeySetResult(
    IReadOnlyList<CompositeKey> MatchingKeys,
    string? ParentWherePredicateSql,
    IReadOnlyDictionary<string, object?> Parameters,
    double QueryCost);
