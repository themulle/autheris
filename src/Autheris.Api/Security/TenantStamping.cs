namespace Autheris.Api.Security;

using System.Runtime.CompilerServices;
using System.Security.Claims;
using Autheris.Application.Tenancy;
using Autheris.Domain.Tenancy;

/// <summary>
/// DT-A-3 / DT-SC-05 / DT-SC-06: stamps the resolved tenant on the identity. The claims chain is authoritative downstream
/// (<c>GetTenantId</c> reads claims only), so the stamped state must be unambiguous: a realm-bound principal gets exactly one
/// <c>tenant_id</c> (token tenant claims are removed: realm binding wins, an <c>Unbound</c> realm ignores them), an invalid realm statement
/// is stamped as a blank (Invalid) claim, and <c>autheris:tenant_source</c> is overwritten (informational only).
/// </summary>
internal static class TenantStamping
{
    // Identities this process stamped, with the source of the first stamping. A repeated transformation of the same identity keeps the
    // original source (the tenant claim it re-reads is the stamped one); an inbound autheris:tenant_source never gets here (identity unknown).
    private static readonly ConditionalWeakTable<ClaimsIdentity, string> Stamped = new();

    public static TenantResolution Apply(ClaimsIdentity identity, ITenantResolver resolver, bool realmBound)
    {
        var restamp = Stamped.TryGetValue(identity, out var originalSource);
        while (identity.FindFirst(TenantSourceClaim.ClaimType) is { } inbound)
        {
            identity.RemoveClaim(inbound);
        }

        var resolution = resolver.Resolve(new ClaimsPrincipal(identity));
        if (realmBound)
        {
            var names = TenantClaimReader.ClaimNames;
            for (var i = 0; i < names.Count; i++)
            {
                while (identity.FindFirst(names[i]) is { } claim)
                {
                    identity.RemoveClaim(claim);
                }
            }
        }

        string? source = null;
        switch (resolution.Outcome)
        {
            case TenantResolutionOutcome.FromClaim when !realmBound:
                source = TenantSourceClaim.Claim;
                if (resolution.Tenant is { } claimTenant && identity.FindFirst("tenant_id") is null)
                {
                    identity.AddClaim(new Claim("tenant_id", claimTenant.Value));
                }

                break;
            case TenantResolutionOutcome.FromClaim:
            case TenantResolutionOutcome.FromRealm:
                source = TenantSourceClaim.Realm;
                identity.AddClaim(new Claim("tenant_id", resolution.Tenant!.Value.Value));
                break;
            case TenantResolutionOutcome.DefaultAssigned:
                source = TenantSourceClaim.Default;
                identity.AddClaim(new Claim("tenant_id", resolution.Tenant!.Value.Value));
                break;
            case TenantResolutionOutcome.Invalid when realmBound:
                // The realm itself named no valid tenant: a blank claim reads back as Invalid (never as absent or the default).
                identity.AddClaim(new Claim("tenant_id", string.Empty));
                break;
        }

        if (restamp && source is not null)
        {
            source = originalSource;
        }

        if (source is not null)
        {
            identity.AddClaim(new Claim(TenantSourceClaim.ClaimType, source));
            Stamped.AddOrUpdate(identity, restamp ? originalSource! : source);
        }

        return resolution;
    }
}
