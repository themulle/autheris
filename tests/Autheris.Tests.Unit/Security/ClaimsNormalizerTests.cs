namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Shouldly;
using Xunit;

public sealed class ClaimsNormalizerTests : IDisposable
{
    public void Dispose()
    {
        Sid.RealmLookup = null;
    }

    [Fact]
    public void RealmBinding_OverwritesIdpTenantIdClaim()
    {
        var boundTenant = new TenantId("tenant-enterprise-bound");
        var realm = new IdentityRealm(
            "enterprise-realm",
            IdentityRealmKind.Customer,
            TenantBindingSpecification.Fixed(boundTenant));

        Sid.RealmLookup = r => r == "enterprise-realm" ? realm : null;

        var identity = new ClaimsIdentity(
        [
            new Claim("autheris:realm", "enterprise-realm"),
            new Claim("tenant_id", "untrusted-idp-tenant-claim"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-100")
        ], "Oidc");
        // DT-A-3: the realm binding is applied by the tenant stamping of the claims transformation (Normalize no longer rewrites tenants).
        DefaultTenantCaller.StampRealm(realm, identity);

        var tenantClaims = identity.FindAll("tenant_id").ToList();
        tenantClaims.Count.ShouldBe(1);
        tenantClaims.Single().Value.ShouldBe(boundTenant.Value);
        identity.FindFirst("autheris:tenant_source")!.Value.ShouldBe("realm");
    }
}
