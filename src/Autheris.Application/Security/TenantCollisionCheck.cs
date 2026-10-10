namespace Autheris.Application.Security;

/// <summary>Tenant ids that differ only in case (stakeholder decision B-1).</summary>
public sealed record TenantCollision(string Canonical, IReadOnlyList<string> Spellings);

/// <summary>
/// Decision B-1 / SEC-ADG-04: tenant ids are case-sensitive (<c>Acme</c> and <c>acme</c> are different tenants) and every dialect
/// compares them byte-exactly. Ids that collide case-insensitively are still a hazard for every consumer that is
/// case-insensitive (caches, logs, operators), so they are reported at startup and the colliding tenants are denied until an
/// operator resolves the collision. Existing ids are never rewritten.
/// </summary>
public static class TenantCollisionCheck
{
    /// <summary>Groups the ids case-insensitively and returns every group that has more than one exact spelling.</summary>
    public static IReadOnlyList<TenantCollision> FindCollisions(IEnumerable<string?> tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        return tenantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new TenantCollision(group.Key.ToUpperInvariant(), group.OrderBy(x => x, StringComparer.Ordinal).ToList()))
            .OrderBy(c => c.Canonical, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The set of tenant ids to deny: every spelling of every colliding group (fail closed).</summary>
    public static IReadOnlySet<string> DeniedTenants(IEnumerable<string?> tenantIds) =>
        FindCollisions(tenantIds).SelectMany(c => c.Spellings).ToHashSet(StringComparer.Ordinal);
}
