using System.Security.Claims;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>Until the group resolver is wired into the pipeline, dropped token group values make the request degraded instead of silently shrinking the group set.</summary>
[Trait("Category", "Security")]
public sealed class InterimTokenGroupDegradeTests
{
    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("oid", "user-oid-1"), .. claims], "Bearer"));

    [Theory]
    [InlineData("11223344-5566-7788-99aa-bbccddeeff00")]
    [InlineData("finance-team")]
    public async Task Dropped_token_group_value_marks_the_request_degraded(string value)
    {
        var transformed = await new EnterpriseClaimsTransformation().TransformAsync(Token(new Claim("groups", "S-1-5-21-1-2-3-1105"), new Claim("groups", value)));

        transformed.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
    }

    [Fact]
    public async Task Canonical_token_groups_are_not_degraded()
    {
        var transformed = await new EnterpriseClaimsTransformation().TransformAsync(Token(new Claim("groups", "S-1-5-21-1-2-3-1105")));

        transformed.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeFalse();
        transformed.GetGroupSids().Select(g => g.Value).ShouldBe(["S-1-5-21-1-2-3-1105"]);
    }
}
