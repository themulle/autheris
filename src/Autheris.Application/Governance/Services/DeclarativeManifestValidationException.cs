namespace Autheris.Application.Governance.Services;

/// <summary>The part of a manifest policy that failed validation.</summary>
public enum DeclarativeManifestViolation
{
    /// <summary>The owner-authored description is invalid.</summary>
    Description = 0,

    /// <summary>A column rule has a blank scope entry or a scope entry that contains a comma.</summary>
    ScopeEntry = 1,

    /// <summary>The grantee type is the retired role type or is not a supported type.</summary>
    GranteeType = 2,

    /// <summary>A proposal names its object by a pattern; a proposal may name objects explicitly only (no `secret` object through a pattern).</summary>
    Pattern = 3
}

/// <summary>
/// A manifest policy failed validation. The message is fixed and names only the policy index and the violated rule; it never carries the
/// rejected text or character.
/// </summary>
public sealed class DeclarativeManifestValidationException(int policyIndex, DeclarativeManifestViolation violation = DeclarativeManifestViolation.Description)
    : ArgumentException(Describe(policyIndex, violation))
{
    public int PolicyIndex { get; } = policyIndex;

    public DeclarativeManifestViolation Violation { get; } = violation;

    /// <summary>The fixed client-facing text for a violation; endpoints build the response from it, never from <see cref="Exception.Message"/>.</summary>
    public static string Describe(int policyIndex, DeclarativeManifestViolation violation) => violation switch
    {
        DeclarativeManifestViolation.ScopeEntry =>
            $"Manifest policy at index {policyIndex} has a column rule with a blank scope entry or a scope entry that contains a comma; list scope names only (an empty list sets the base level for all scopes).",
        DeclarativeManifestViolation.GranteeType =>
            $"Manifest policy at index {policyIndex} has an unsupported grantee type; use user, group or service principal.",
        DeclarativeManifestViolation.Pattern =>
            $"Manifest policy at index {policyIndex} names its object by a pattern; a proposal must name every domain, schema and table explicitly.",
        _ => $"Manifest policy at index {policyIndex} has an invalid description.",
    };
}
