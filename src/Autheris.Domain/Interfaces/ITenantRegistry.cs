namespace Autheris.Domain.Interfaces;

using Autheris.Domain.Common;

/// <summary>Outcome of a tenant registration (CR-ADG-31, WP-X0p).</summary>
public enum TenantRegistration
{
    /// <summary>The tenant id was seen for the first time and has been recorded.</summary>
    Registered,

    /// <summary>The exact spelling is already recorded and its group is not marked as colliding.</summary>
    Existing,

    /// <summary>The id differs only in case/normalization from another recorded spelling; every spelling of the group is denied.</summary>
    Collision,

    /// <summary>The issuer exceeded its budget for new registrations (fail closed, nothing was written).</summary>
    RateLimited
}

/// <summary>
/// CR-ADG-31: registry of every tenant id the gateway has seen, including tenants that exist only in the identity provider.
/// Ids are grouped by <see cref="TenantRegistryKey.Normalize"/>; a second spelling in a group marks the whole group as colliding.
/// An unavailable registry throws (the caller fails closed); implementations never report an allow when they cannot decide.
/// </summary>
public interface ITenantRegistry
{
    /// <param name="tenant">A validated tenant id of an authenticated principal.</param>
    /// <param name="issuer">
    /// The budget key that rate-limits new registrations: the identity provider issuer, or <c>scheme:&lt;authentication scheme&gt;</c> for a
    /// token without an issuer (one bucket per scheme, never one shared bucket for all issuer-less tokens).
    /// </param>
    ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct);

    /// <summary>
    /// Phase 5 L6: a read-only lookup that never writes and never consumes a registration budget. <see cref="TenantRegistration.Existing"/>
    /// when this exact spelling is recorded and its group does not collide, <see cref="TenantRegistration.Collision"/> when the group is
    /// marked, and <c>null</c> when the tenant is unknown or is another spelling of a known group (only <see cref="RegisterOrGetAsync"/>
    /// may register a tenant or mark a collision). The default (a store without a lookup) is "unknown".
    /// </summary>
    ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct) => ValueTask.FromResult<TenantRegistration?>(null);

    /// <summary>
    /// M-A2: operator resolution of a collided group. Keeps <paramref name="keep"/> (exact spelling) as the tenant of its group and clears the
    /// collision mark; every other spelling of the group is denied again (the lookup treats it as unknown and a registration marks the group
    /// anew). Changes nothing, and reports <see cref="TenantCollisionResolutionStatus.NotFound"/>, when the group is not marked (so it cannot
    /// rename a healthy tenant). L-4: the spelling that the group held before is read first and returned (the operation overwrites it), and
    /// with <paramref name="expectedCurrent"/> the change is a compare-and-swap: a group that holds another spelling is left unchanged
    /// (<see cref="TenantCollisionResolutionStatus.ExpectedMismatch"/>). The default (a store without a resolution path) is "not found".
    /// </summary>
    ValueTask<TenantCollisionResolution> ResolveCollisionAsync(TenantId keep, string? expectedCurrent, CancellationToken ct) =>
        ValueTask.FromResult(new TenantCollisionResolution(TenantCollisionResolutionStatus.NotFound, null));

    /// <summary>Records the configured tenant ids (not rate limited); configured spellings that collide mark their group.</summary>
    ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct);
}

/// <summary>The outcome of <see cref="ITenantRegistry.ResolveCollisionAsync"/>.</summary>
public enum TenantCollisionResolutionStatus
{
    /// <summary>No collided group exists for the tenant id: nothing changed.</summary>
    NotFound,

    /// <summary>The group holds another spelling than the one the operator expected (or it changed meanwhile): nothing changed.</summary>
    ExpectedMismatch,

    /// <summary>The collision mark is cleared and the kept spelling is the group's tenant.</summary>
    Resolved
}

/// <summary>The status and the spelling that the group held before the operation (null when there is no collided group).</summary>
public readonly record struct TenantCollisionResolution(TenantCollisionResolutionStatus Status, string? PreviousTenantId);
