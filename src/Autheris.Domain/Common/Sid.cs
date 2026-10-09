using System;

namespace Autheris.Domain.Common;

public readonly record struct Sid(string Value) : IEquatable<Sid>
{
    public override string ToString() => Value ?? string.Empty;

    public static implicit operator string(Sid sid) => sid.Value;
    public static implicit operator Sid(string value) => new(value);

    public bool Equals(Sid other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);
}

public static class ClaimsPrincipalExtensions
{
    public static Sid? GetUserSid(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return null;

        var sidStr = principal.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value
            ?? principal.FindFirst("objectSid")?.Value
            ?? principal.FindFirst("onprem_sid")?.Value
            ?? principal.FindFirst("primarysid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid")?.Value
            ?? principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.FindFirst("appid")?.Value
            ?? principal.FindFirst("client_id")?.Value
            ?? principal.FindFirst("azp")?.Value;

        return string.IsNullOrWhiteSpace(sidStr) ? (Sid?)null : new Sid(sidStr);
    }

    private static readonly string[] ImmutableUserSidClaimTypes =
    [
        System.Security.Claims.ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        System.Security.Claims.ClaimTypes.NameIdentifier,
        "sub"
    ];

    private static readonly string[] UserIdentifierClaimTypes =
    [
        System.Security.Claims.ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        System.Security.Claims.ClaimTypes.NameIdentifier,
        "sub",
        System.Security.Claims.ClaimTypes.Upn,
        "upn",
        "preferred_username",
        System.Security.Claims.ClaimTypes.Name,
        "name"
    ];

    /// <summary>
    /// SEC C-05: Returns every user-bound identifier of the principal (SIDs, oid, sub, upn, NameIdentifier, name).
    /// Used for Four-Eyes checks: if any identifier of an approver equals the requester identity, the approval is a self-approval.
    /// Application identifiers (appid, client_id, azp) are deliberately excluded because they are shared by all users of a client.
    /// </summary>
    public static HashSet<string> GetUserIdentifiers(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (principal == null) return result;

        var primary = principal.GetUserSid();
        if (primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value.Value))
        {
            result.Add(primary.Value.Value);
        }

        foreach (var claimType in UserIdentifierClaimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    result.Add(claim.Value.Trim());
                }
            }
        }

        var identityName = principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(identityName))
        {
            result.Add(identityName.Trim());
        }

        return result;
    }

    /// <summary>
    /// SG-04: Returns all user-bound immutable subject identifiers resolved as Sid objects.
    /// Used for matching access profiles and consents bound to on-prem SID, cloud OID, or sub.
    /// Mutable/free-text claims (name, preferred_username, upn) are strictly excluded from grant/consent matching
    /// to prevent consent impersonation attacks.
    /// </summary>
    public static HashSet<Sid> GetAllUserSids(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        var result = new HashSet<Sid>();
        if (principal == null) return result;

        var primary = principal.GetUserSid();
        if (primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value.Value))
        {
            result.Add(primary.Value);
        }

        foreach (var claimType in ImmutableUserSidClaimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    result.Add(new Sid(claim.Value.Trim()));
                }
            }
        }

        return result;
    }

    public static HashSet<Sid> GetGroupSids(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return [];

        return principal.FindAll(System.Security.Claims.ClaimTypes.GroupSid)
            .Concat(principal.FindAll("groups"))
            .Concat(principal.FindAll("groupsid"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid"))
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => new Sid(c.Value))
            .ToHashSet();
    }

    public static HashSet<string> GetUserRoles(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return principal.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Concat(principal.FindAll("roles"))
            .Concat(principal.FindAll("role"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/role"))
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static TenantId GetTenantId(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return TenantId.LegacySingleTenant;

        var val = principal.FindFirst("tenant_id")?.Value
            ?? principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("tenant")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        if (string.IsNullOrWhiteSpace(val))
        {
            return TenantId.LegacySingleTenant;
        }

        if (TenantId.TryParse(val, out var parsed))
        {
            return parsed;
        }

        // API-7 / D-6: the raw claim value is never echoed (it reaches logs and, via the security context middleware, clients).
        throw new System.Security.SecurityException("Invalid tenant claim value. The tenant identifier contains illegal characters or does not meet format requirements.");
    }
}
