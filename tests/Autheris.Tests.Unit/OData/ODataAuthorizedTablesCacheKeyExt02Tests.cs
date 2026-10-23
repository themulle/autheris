namespace Autheris.Tests.Unit.OData;

using System.Security.Claims;
using Autheris.Extensions.OData;
using Shouldly;
using Xunit;

/// <summary>EXT-02: the authorized-tables cache key covers the whole principal and refuses SID-less callers.</summary>
[Trait("Category", "Security")]
public sealed class ODataAuthorizedTablesCacheKeyExt02Tests
{
    private static ClaimsPrincipal User(string? sid, params string[] roles)
    {
        var claims = new System.Collections.Generic.List<Claim> { new("tenant_id", "erp") };
        if (sid != null)
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, sid));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public void SameSid_DifferentRoles_DifferentKeys()
    {
        var plain = ODataHandler.BuildAuthorizedTablesCacheKey(User("S-1-5-21-1"));
        var elevated = ODataHandler.BuildAuthorizedTablesCacheKey(User("S-1-5-21-1", "BreakGlass"));

        plain.ShouldNotBeNull();
        elevated.ShouldNotBeNull();
        elevated.ShouldNotBe(plain);
    }

    [Fact]
    public void RoleOrder_DoesNotChangeTheKey()
    {
        ODataHandler.BuildAuthorizedTablesCacheKey(User("S-1-5-21-1", "A", "B"))
            .ShouldBe(ODataHandler.BuildAuthorizedTablesCacheKey(User("S-1-5-21-1", "B", "A")));
    }

    [Fact]
    public void PrincipalWithoutSid_IsNeverCached()
    {
        ODataHandler.BuildAuthorizedTablesCacheKey(User(null)).ShouldBeNull();
    }
}
