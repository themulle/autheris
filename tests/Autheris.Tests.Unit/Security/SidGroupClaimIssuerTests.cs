using System.Security.Claims;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class SidGroupClaimIssuerTests
{
    private const string Sid1 = "S-1-5-21-1-2-3-1105";

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03b")]
    public void GetGroupSids_ignores_groups_and_groupsid_claim_types()
    {
        var principal = Principal(
            new Claim("groups", Sid1, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline),
            new Claim("groupsid", Sid1, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline),
            new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups", Sid1, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));

        principal.GetGroupSids().ShouldBeEmpty();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03b")]
    public void GetGroupSids_ignores_groupsid_claim_with_foreign_issuer() =>
        Principal(new Claim(ClaimTypes.GroupSid, Sid1, ClaimValueTypes.String, "https://login.example.org/")).GetGroupSids().ShouldBeEmpty();

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03b")]
    public void GetGroupSids_ignores_local_authority_issuer() =>
        Principal(new Claim(ClaimTypes.GroupSid, Sid1)).GetGroupSids().ShouldBeEmpty();

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03b")]
    public void GetGroupSids_reads_pipeline_issuer_only()
    {
        var principal = Principal(
            IdentityClaimIssuers.CreateGroupSidClaim(Sid1),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-1106", ClaimValueTypes.String, "other"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-1107", ClaimValueTypes.String, IdentityClaimIssuers.Pipeline.ToLowerInvariant()));

        principal.GetGroupSids().Select(s => s.Value).ShouldBe([Sid1]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03b")]
    public void GetGroupSids_drops_non_canonical_values()
    {
        var principal = Principal(
            IdentityClaimIssuers.CreateGroupSidClaim(Sid1),
            IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-FORWARD-GRP-finance"),
            IdentityClaimIssuers.CreateGroupSidClaim("11111111-2222-3333-4444-555555555555"),
            IdentityClaimIssuers.CreateGroupSidClaim(""));

        principal.GetGroupSids().Select(s => s.Value).ShouldBe([Sid1]);
    }
}
