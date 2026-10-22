namespace Autheris.Application.Governance.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>What a stored plan changes when it is applied (issue #107, SEC-RF107-14c): each apply path accepts only its own kind.</summary>
public enum AccessPlanKind
{
    /// <summary>Access grants (ReBAC tuples); the default of every plan stored before issue #107.</summary>
    Access = 0,

    /// <summary>Row filter key-set cache settings of one rule or one object.</summary>
    RowFilterEvaluation = 1,

    /// <summary>
    /// A mutating MCP administrator operation (dataset state, dataset catalog settings, datasource registration), bound to the tool
    /// and a digest of its arguments (<see cref="AdminOperationPlan"/>).
    /// </summary>
    AdminOperation = 2
}

/// <summary>
/// The operation of an <see cref="AccessPlanKind.AdminOperation"/> plan: the MCP tool and the SHA-256 digest of its canonical arguments.
/// The confirmation token of the plan applies exactly this operation with exactly these arguments.
/// </summary>
public sealed record AdminOperationPlan(string Operation, string ArgumentsDigest);

/// <summary>
/// The change of a <see cref="AccessPlanKind.RowFilterEvaluation"/> plan. <paramref name="Before"/> is the stored setting at planning time;
/// apply rejects the plan when the stored setting differs then (optimistic concurrency). <paramref name="RowFilterId"/> is set for the rule
/// level, <paramref name="Table"/> is the target table (<c>domain.schema.table</c>) of both levels.
/// </summary>
public sealed record RowFilterEvaluationPlan(
    Guid? RowFilterId,
    string Table,
    RowFilterEvaluationSettings Before,
    RowFilterEvaluationSettings After);

/// <summary>
/// A pending access plan as persisted by an <see cref="IAccessPlanStore"/>. Only a SHA-256 hash of the confirmation
/// token is stored, never the token itself. <see cref="PlanKind"/> separates access plans from row filter evaluation plans
/// (<see cref="RowFilterEvaluation"/> is set only for the latter; their <see cref="Request"/> carries no grants).
/// <see cref="AdminOperation"/> is set only for <see cref="AccessPlanKind.AdminOperation"/> plans.
/// </summary>
public sealed record StoredAccessPlan(
    AdminPlanAccessResult Result,
    AdminPlanAccessRequest Request,
    string AdminSid,
    string? ConfirmedTokenHash = null,
    string? TenantId = null,
    AccessPlanKind PlanKind = AccessPlanKind.Access,
    RowFilterEvaluationPlan? RowFilterEvaluation = null,
    AdminOperationPlan? AdminOperation = null);

/// <summary>
/// State port of the two-phase access planning service (SR-L-06, ARC-C-05). Plans and consumed confirmation tokens are
/// short-lived (TTL) and, when the deployment is clustered, shared by all replicas. Implementations backed by a remote
/// store throw <see cref="Autheris.Application.State.ClusterStateUnavailableException"/> when the store cannot be reached;
/// the service then fails the operation (fail closed) instead of treating the state as absent.
/// </summary>
public interface IAccessPlanStore
{
    /// <summary>
    /// Saves a new plan unless the administrator already holds <paramref name="maxPendingPerAdmin"/> unexpired plans.
    /// Returns false when the cap is exceeded.
    /// </summary>
    ValueTask<bool> TrySavePlanAsync(StoredAccessPlan plan, TimeSpan ttl, int maxPendingPerAdmin, CancellationToken ct = default);

    /// <summary>Returns the plan, or null when it is absent or expired.</summary>
    ValueTask<StoredAccessPlan?> GetPlanAsync(string planId, CancellationToken ct = default);

    /// <summary>Records the hash of the confirmation token issued for the plan. Returns false when the plan is gone.</summary>
    ValueTask<bool> SetConfirmedTokenAsync(string planId, string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks the token hash as consumed (first caller wins cluster-wide). Returns false when it was already
    /// consumed.
    /// </summary>
    ValueTask<bool> TryConsumeTokenAsync(string tokenHash, TimeSpan ttl, CancellationToken ct = default);
}
