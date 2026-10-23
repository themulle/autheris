using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>PERS-08: an unsupported SCIM filter is rejected instead of silently returning unfiltered pages.</summary>
public sealed class ScimFilterGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoFilter_IsNotRejected(string? filter) =>
        ScimFilterGuard.RejectUnsupportedFilter(filter).ShouldBeNull();

    [Fact]
    public void Filter_IsRejectedWith400()
    {
        var result = ScimFilterGuard.RejectUnsupportedFilter("userName eq \"x\"");

        result.ShouldNotBeNull();
        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }
}
