namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;

/// <summary>
/// Whether a grant (consent or access profile) addresses the caller: users and service principals by SID, groups by
/// group SID, roles by name (or role id). One rule for consents and virtual filter profiles.
/// </summary>
public static class GranteeMatcher
{
    public static bool Matches(
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
