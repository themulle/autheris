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

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "enterprise-realm"),
            new Claim("tenant_id", "untrusted-idp-tenant-claim"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-100")
        ], "Oidc"));

        var normalized = ClaimsNormalizer.Normalize(principal);

        var tenantClaims = normalized.FindAll("tenant_id");
        tenantClaims.Count().ShouldBe(1);
        tenantClaims.Single().Value.ShouldBe(boundTenant.Value);
    }
}
