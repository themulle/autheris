using Autheris.Api.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>API-07: SCIM routes are authenticated by the Scim scheme instead of being charged as failed JWT requests.</summary>
public sealed class ScimBearerRoutingTests
{
    [Theory]
    [InlineData("/scim/v2/realm-1/Users", true)]
    [InlineData("/SCIM/v2/realm-1/Groups", true)]
    [InlineData("/scim", true)]
    [InlineData("/graphql", false)]
    [InlineData("/scimitar/v2", false)]
    [InlineData("/api/scim/v2", false)]
    public void IsScimPath_MatchesOnlyTheScimSegment_API07(string path, bool expected) =>
        GatewayAuthSchemes.IsScimPath(new PathString(path)).ShouldBe(expected);
}
