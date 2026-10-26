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
    public static readonly IReadOnlyList<string> ClaimNames =
    [
        "tenant_id",
        "tid",
        "tenant",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];

    public static ClaimTenant Read(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return ClaimTenant.Absent;
        }

        string? seen = null;
        foreach (var name in ClaimNames)
        {
            foreach (var claim in principal.FindAll(name))
            {
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
