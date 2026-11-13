namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;

/// <summary>
/// Whether a grantee addresses the caller. Grants (consents, write grants) match users, service principals and groups by SID and
/// never match a role. Restrictions (virtual filter profiles) also match roles by name or id, so a role-bound restriction keeps applying.
/// </summary>
public static class GranteeMatcher
{
    /// <summary>Whether a consent or write-grant grantee addresses the caller; a role grantee never matches.</summary>
    public static bool MatchesGrant(
        GranteeType granteeType,
        Sid? granteeSid,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<Sid>? allUserSids = null) =>
        granteeType != GranteeType.Role &&
        MatchesRestriction(granteeType, granteeSid, null, null, userSid, groupSids, EmptyRoles, allUserSids);

    private static readonly IReadOnlySet<string> EmptyRoles = new HashSet<string>(0);

    /// <summary>Whether a restriction grantee addresses the caller; roles match by name or id.</summary>
    public static bool MatchesRestriction(
        GranteeType granteeType,
        Sid? granteeSid,
        string? roleName,
        Guid? roleId,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> roles,
        IReadOnlySet<Sid>? allUserSids = null)
    {
        ArgumentNullException.ThrowIfNull(groupSids);
        ArgumentNullException.ThrowIfNull(roles);

        return granteeType switch
        {
            GranteeType.User or GranteeType.ServicePrincipal =>
                granteeSid.HasValue && (granteeSid.Value == userSid || (allUserSids != null && allUserSids.Contains(granteeSid.Value))),
            GranteeType.Group => granteeSid.HasValue && groupSids.Contains(granteeSid.Value),
            GranteeType.Role => (!string.IsNullOrEmpty(roleName) && roles.Contains(roleName)) ||
                                (roleId.HasValue && roles.Contains(roleId.Value.ToString())),
            _ => false
        };
    }
}
