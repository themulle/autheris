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
    /// <param name="issuer">The identity provider issuer, used to rate-limit new registrations per issuer.</param>
    ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct);

    /// <summary>Records the configured tenant ids (not rate limited); configured spellings that collide mark their group.</summary>
    ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct);
}
