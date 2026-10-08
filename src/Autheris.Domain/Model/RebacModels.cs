#pragma warning disable CA1720 // Identifier contains type name ('Object' is standard Google Zanzibar terminology)

namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// F-SEC-04: Google Zanzibar / OpenFGA Relationship-Based Access Control (ReBAC) Models.
/// Represents relationship tuples, check queries, and batch results for multi-tenant entity authorization.
/// </summary>

/// <summary>
/// Immutable tuple representing a direct relationship: (TenantId, User, Relation, Object).
/// E.g. ("tenant-1", "user:alice", "viewer", "document:doc-101")
/// </summary>
public sealed record RebacTuple(
    string TenantId,
    string User,
    string Relation,
    string Object
)
{
    public override string ToString() => $"({TenantId}, {User}, {Relation}, {Object})";
}

/// <summary>
/// Request to check if a specific user has a relation with an object within a tenant boundary.
/// </summary>
public sealed record RebacCheckRequest(
    string TenantId,
    string User,
    string Relation,
    string Object
);

/// <summary>
/// Evaluation result for a ReBAC authorization check.
/// </summary>
public sealed record RebacCheckResult(
    bool Allowed,
    string? Reason = null
)
{
    public static readonly RebacCheckResult Permitted = new(true);
    public static readonly RebacCheckResult Denied = new(false, "Authorization denied by ReBAC policy.");
}

/// <summary>
/// Batch request containing multiple ReBAC check tuples.
/// Used by DataLoaders to eliminate N+1 latency.
/// </summary>
public sealed record RebacBatchCheckRequest(
    string TenantId,
    IReadOnlyList<RebacCheckRequest> Checks
);

/// <summary>
/// Batched evaluation response mapping each check request to its authorization decision.
/// </summary>
public sealed record RebacBatchCheckResult(
    IReadOnlyDictionary<RebacCheckRequest, bool> Decisions
);

