namespace Autheris.Application.Common.Interfaces;

/// <summary>Why a system revocation ends a grant; decides whether consent event consumers learn about it.</summary>
public enum SystemRevocationKind
{
    /// <summary>Access ends (deprovisioning): the revocation appends a grant.revoked consent event.</summary>
    Revoked = 0,

    /// <summary>
    /// Part of a grant renewal: the superseded grant is replaced by the renewed one, or a renewed grant is rolled back while the
    /// original stays active. Access does not end, so no grant.revoked is appended.
    /// </summary>
    Renewal = 1
}
