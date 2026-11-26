namespace Autheris.Application.Common.Interfaces;

/// <summary>Why a system revocation ends a grant; decides whether consent event consumers learn about it.</summary>
public enum SystemRevocationKind
{
    /// <summary>Access ends (deprovisioning): the revocation appends a grant.revoked consent event.</summary>
    Revoked = 0,

    /// <summary>
    /// Supersedes a grant during a renewal: the superseded grant ends without a grant.revoked, and a grant.renewed is appended for the
    /// renewed grant (identified by the renewed consent id). Access continues through the renewed grant.
    /// </summary>
    Renewal = 1,

    /// <summary>
    /// Compensation of a renewal that did not complete: the renewed grant is revoked. A grant.revoked is appended only when a grant.renewed
    /// for that grant was committed, so consumers never see a revoke for a grant they were not told about.
    /// </summary>
    RenewalRollback = 2
}
