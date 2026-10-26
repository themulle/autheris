namespace Autheris.Domain.Tenancy;

using System.Security.Claims;
using Autheris.Domain.Common;

/// <summary>
/// DT-SC-02: the one reader of the standard tenant claim chain (replaces the per-caller <c>tenant_id</c>/<c>tid</c>/<c>tenant</c>
/// chains). Several distinct values on one principal are <see cref="ClaimTenantKind.Invalid"/> (never first-wins); a blank,
/// unparsable or reserved value is Invalid, never Absent.
/// </summary>
public static class TenantClaimReader
{
    /// <summary>The claim names read, in one place.</summary>
    private static readonly string[] Names =
    [
        "tenant_id",
        "tid",
        "tenant",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];

    public static IReadOnlyList<string> ClaimNames => Names;

    public static bool IsTenantClaimType(string? type)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], type, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static ClaimTenant Read(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return ClaimTenant.Absent;
        }

        // Fast path (claim-less principals, the common default-tenant case): FindFirst does not allocate.
        var any = false;
        for (var i = 0; i < Names.Length; i++)
        {
            if (principal.FindFirst(Names[i]) is not null)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return ClaimTenant.Absent;
        }

        string? seen = null;
        var identities = principal.Identities;
        foreach (var identity in identities)
        {
            foreach (var claim in identity.Claims)
            {
                if (!IsTenantClaimType(claim.Type))
                {
                    continue;
                }

                if (seen is null)
                {
                    seen = claim.Value ?? string.Empty;
                }
                else if (!string.Equals(seen, claim.Value, StringComparison.Ordinal))
                {
                    return ClaimTenant.Invalid;
                }
            }
        }

        if (seen is null)
        {
            return ClaimTenant.Absent;
        }

        return TenantId.TryParse(seen, out var tenant) ? ClaimTenant.Valid(tenant) : ClaimTenant.Invalid;
    }
}
