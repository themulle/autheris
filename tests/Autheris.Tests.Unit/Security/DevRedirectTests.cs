namespace Autheris.Tests.Unit.Security;

using Autheris.Api.Endpoints;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-05 (A-2): the dev login redirect accepts only plain same-site paths.</summary>
public sealed class DevRedirectTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/graphql")]
    [InlineData("/graphql?x=1&y=2")]
    public void IsLocalPath_PlainPaths_AreAccepted(string target)
    {
        DevEndpoints.IsLocalPath(target).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]     // browsers strip the tab and see //evil.example
    [InlineData("/\n/evil.example")]
    [InlineData("/ /evil.example")]
    [InlineData("/a\\b")]
    [InlineData("https://evil.example")]
    [InlineData("evil")]
    public void IsLocalPath_OpenRedirectVariants_AreRejected(string? target)
    {
        DevEndpoints.IsLocalPath(target).ShouldBeFalse();
    }

    [Theory]
    [InlineData("cross-site", true)]
    [InlineData("same-site", true)]
    [InlineData("none", false)]          // typed URL, bookmark, terminal link
    [InlineData("same-origin", false)]
    [InlineData("", false)]              // not a browser (curl, tests)
    public void IsCrossSiteRequest_UsesSecFetchSite(string header, bool expected)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (header.Length > 0)
        {
            ctx.Request.Headers["Sec-Fetch-Site"] = header;
        }

        DevEndpoints.IsCrossSiteRequest(ctx.Request).ShouldBe(expected);
    }
}
