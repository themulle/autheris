namespace Autheris.Domain.Model;

using Autheris.Domain.Security;

/// <summary>
/// REQ-GOV-143: carried by a decision for a caller that holds restricted scopes. A catalog-sensitive column is only in clear when it is
/// also in <see cref="ClearedColumns"/> (a consent <c>Clear</c> written for every restricted scope held). <c>null</c> on the decision = unrestricted caller.
/// </summary>
/// <param name="RestrictedScopesHeld">The restricted scopes the caller holds (a non-empty marker; never used for access).</param>
/// <param name="ClearedColumns">Columns whose level in the same decision is <c>Clear</c> and that carry a scope-specific <c>Clear</c> for every held scope.</param>
public sealed record RestrictedScopeClearance(IReadOnlySet<string> RestrictedScopesHeld, IReadOnlySet<string> ClearedColumns)
{
    /// <summary>True when <paramref name="column"/> may be in clear for this caller (case-insensitive).</summary>
    public bool Permits(string column) => column is not null && ClearedColumns.Contains(column);

    /// <summary>
    /// A caller whose scopes are unknown (no claims) is treated as holding every restricted scope with no clearance (PLAN-GOV-143 A-1).
    /// <c>null</c> when the policy restricts nothing.
    /// </summary>
    public static RestrictedScopeClearance? Unknown(IRestrictedScopePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Scopes.Count == 0
            ? null
            : new RestrictedScopeClearance(
                new HashSet<string>(policy.Scopes, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
