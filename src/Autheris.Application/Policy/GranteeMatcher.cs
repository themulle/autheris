namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;

/// <summary>
/// Whether a grantee addresses the caller. Users, service principals and groups match by SID; no other grantee kind exists.
/// An undefined grantee type never grants (no match) and never lets a restriction be skipped: a restriction check throws,
/// so the decision that needed it denies.
/// </summary>
public static class GranteeMatcher
{
    /// <summary>Whether a consent or write-grant grantee addresses the caller.</summary>
    public static bool MatchesGrant(
        GranteeType granteeType,
        Sid? granteeSid,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<Sid>? allUserSids = null) =>
        Enum.IsDefined(granteeType) && MatchesRestriction(granteeType, granteeSid, userSid, groupSids, allUserSids);

    /// <summary>Whether a restriction grantee (virtual filter profile) addresses the caller.</summary>
    public static bool MatchesRestriction(
        GranteeType granteeType,
        Sid? granteeSid,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<Sid>? allUserSids = null)
    {
        ArgumentNullException.ThrowIfNull(groupSids);

        return granteeType switch
        {
            GranteeType.User or GranteeType.ServicePrincipal =>
                granteeSid.HasValue && (granteeSid.Value == userSid || (allUserSids != null && allUserSids.Contains(granteeSid.Value))),
            GranteeType.Group => granteeSid.HasValue && groupSids.Contains(granteeSid.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(granteeType), "The grantee type is not supported.")
        };
    }
}
