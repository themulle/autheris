namespace Autheris.Application.Governance.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// A pending access plan as persisted by an <see cref="IAccessPlanStore"/>. Only a SHA-256 hash of the confirmation
/// token is stored, never the token itself.
/// </summary>
public sealed record StoredAccessPlan(
    AdminPlanAccessResult Result,
    AdminPlanAccessRequest Request,
    string AdminSid,
    string? ConfirmedTokenHash = null,
    string? TenantId = null);

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
