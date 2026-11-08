namespace Autheris.Tests.Unit.Identity;

using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>Issue #469: application identifiers never stand in for a user subject, and the realm lookup is not a static global.</summary>
public sealed class SidFallbackTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "TestAuth"));

    [Theory]
    [InlineData("appid")]
    [InlineData("client_id")]
    [InlineData("azp")]
    public void GetUserSid_without_sub_or_oid_does_not_use_client_id(string appClaim)
    {
        var principal = Principal(new Claim(appClaim, "shared-client"));

        principal.GetUserSid().ShouldBeNull();
    }

    [Fact]
    public void GetAllUserSids_excludes_app_identifiers()
    {
        var principal = Principal(
            new Claim("oid", "user-oid-1"),
            new Claim("appid", "shared-client"),
            new Claim("client_id", "shared-client"),
            new Claim("azp", "shared-client"));

        var sids = principal.GetAllUserSids();

        sids.ShouldContain(new Sid("user-oid-1"));
        sids.ShouldNotContain(new Sid("shared-client"));
        Principal(new Claim("client_id", "shared-client")).GetAllUserSids().ShouldBeEmpty();
    }

    [Fact]
    public void RealmLookup_is_injected_not_static()
    {
        var statics = new[] { typeof(Sid), typeof(ClaimsPrincipalExtensions) }
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.Name.Contains("RealmLookup", System.StringComparison.Ordinal));

        statics.ShouldBeEmpty();
        new IdentityRealmRegistry().ShouldNotBeNull();
    }
}
