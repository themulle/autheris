namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Application.Security;
using Shouldly;
using Xunit;

public sealed class SrMOutboundPathPolicyTests
{
    private static readonly Uri Base = new("https://api.example.com/v1/");

    [Theory]
    [InlineData("\\/evil.com/x")]
    [InlineData("http:\\\\evil.com")]
    [InlineData("https:\\\\evil.com")]
    [InlineData("//evil.com/x")]
    [InlineData("https://evil.com/x")]
    [InlineData("/https://evil.com/x")]
    [InlineData("HTTP://evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/x@evil.com")]
    [InlineData("/x\r\nHost: evil")]
    [InlineData("/x\0")]
    [InlineData("../../etc/passwd")]
    public void SR_M_14_SR_M_15_UnsafePaths_AreRejected(string path)
    {
        OutboundPathPolicy.TryBuild(Base, path, preserveBasePath: false, allowDotSegments: false, out var target, out var error).ShouldBeFalse();
        target.ShouldBeNull();
        error.ShouldNotBeNull();

        OutboundPathPolicy.TryBuild(Base, path, preserveBasePath: true, allowDotSegments: path.Contains("..", StringComparison.Ordinal), out _, out _)
            .ShouldBe(path.Contains("..", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/health", "https://api.example.com/health")]
    [InlineData("items?id=1", "https://api.example.com/v1/items?id=1")]
    [InlineData("/", "https://api.example.com/")]
    public void SR_M_14_SafePaths_AreCombinedOnSameAuthority(string path, string expected)
    {
        OutboundPathPolicy.TryBuild(Base, path, preserveBasePath: false, allowDotSegments: false, out var target, out _).ShouldBeTrue();
        target!.ToString().ShouldBe(expected);
    }

    [Fact]
    public void SR_M_15_PreserveBasePath_KeepsPrefixAndAuthority()
    {
        OutboundPathPolicy.TryBuild(Base, "/orders/42?x=1", preserveBasePath: true, allowDotSegments: true, out var target, out _).ShouldBeTrue();
        target!.ToString().ShouldBe("https://api.example.com/v1/orders/42?x=1");
    }
}
