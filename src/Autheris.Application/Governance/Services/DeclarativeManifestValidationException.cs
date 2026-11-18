namespace Autheris.Application.Governance.Services;

/// <summary>The part of a manifest policy that failed validation.</summary>
public enum DeclarativeManifestViolation
{
    /// <summary>The owner-authored description is invalid.</summary>
    Description = 0,

    /// <summary>A column rule has a blank scope entry or a scope entry that contains a comma.</summary>
    ScopeEntry = 1
}

/// <summary>
/// A manifest policy failed validation. The message is fixed and names only the policy index and the violated rule; it never carries the
/// rejected text or character.
/// </summary>
public sealed class DeclarativeManifestValidationException(int policyIndex, DeclarativeManifestViolation violation = DeclarativeManifestViolation.Description)
    : ArgumentException(BuildMessage(policyIndex, violation))
{
    public int PolicyIndex { get; } = policyIndex;

    public DeclarativeManifestViolation Violation { get; } = violation;

    private static string BuildMessage(int policyIndex, DeclarativeManifestViolation violation) => violation switch
    {
        DeclarativeManifestViolation.ScopeEntry =>
            $"Manifest policy at index {policyIndex} has a column rule with a blank scope entry or a scope entry that contains a comma; list scope names only (an empty list sets the base level for all scopes).",
        _ => $"Manifest policy at index {policyIndex} has an invalid description.",
    };
}
