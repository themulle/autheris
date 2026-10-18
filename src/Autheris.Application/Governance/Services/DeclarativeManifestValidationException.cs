namespace Autheris.Application.Governance.Services;

/// <summary>
/// A manifest policy failed validation of owner-authored text (PLAN-GOV-158-INC1, SEC-GOV158I1-03). The message is fixed and names only the
/// policy index; it never carries the rejected text or character.
/// </summary>
public sealed class DeclarativeManifestValidationException(int policyIndex)
    : ArgumentException($"Manifest policy at index {policyIndex} has an invalid description.")
{
    public int PolicyIndex { get; } = policyIndex;
}
