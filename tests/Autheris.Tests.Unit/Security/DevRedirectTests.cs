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
}
