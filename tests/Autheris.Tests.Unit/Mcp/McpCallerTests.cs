namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>SEC-OI-508-6: the claim order of a token is IdP-controlled, so a multi-valued scalar attribute is ambiguous, never first-wins.</summary>
[Trait("Category", "Security")]
public sealed class McpCallerTests
{
    private static McpEndpoints.McpCaller Resolve(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new("tenant_id", "tenant_a") };
        claims.AddRange(extra);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        return McpEndpoints.ResolveCaller(ctx, allowOpenMcp: false);
    }

    [Fact]
    public void Multi_valued_claim_is_ambiguous_not_first()
    {
        var caller = Resolve(new Claim("department", "public"), new Claim("department", "finance"), new Claim("region", "eu"));

        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims.ShouldNotContainKey("department");
        caller.AdditionalClaims["region"].ShouldBe("eu");
    }

    [Fact]
    public void Claim_order_does_not_change_the_outcome()
    {
        var a = Resolve(new Claim("department", "public"), new Claim("department", "finance"));
        var b = Resolve(new Claim("department", "finance"), new Claim("department", "public"));

        a.AdditionalClaims.ShouldNotBeNull();
        b.AdditionalClaims.ShouldNotBeNull();
        a.AdditionalClaims.ShouldNotContainKey("department");
        b.AdditionalClaims.ShouldNotContainKey("department");
    }

    [Fact]
    public void Single_valued_claim_is_kept()
    {
        var caller = Resolve(new Claim("department", "finance"));
        caller.AdditionalClaims.ShouldNotBeNull();
        caller.AdditionalClaims["department"].ShouldBe("finance");
    }
}
