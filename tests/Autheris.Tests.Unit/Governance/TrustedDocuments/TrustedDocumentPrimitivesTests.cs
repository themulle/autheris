namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using Autheris.Api.Middleware;
using Autheris.Application.Governance.TrustedDocuments;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP2: exact-text hash, app identity header and origin normalisation (SEC-281-1, -3, -5).</summary>
[Trait("Category", "Security")]
public sealed class TrustedDocumentPrimitivesTests
{
    [Fact]
    public void ExactHash_KnownVector_IsLowerCaseSha256OfUtf8()
    {
        // SHA-256("abc")
        TrustedDocumentHash.ComputeExact("abc"u8).ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        TrustedDocumentHash.ComputeExact("abc").ShouldBe(TrustedDocumentHash.ComputeExact("abc"u8));
    }

    [Theory]
    [InlineData("{ a }", "{  a }")]
    [InlineData("{ a }", "{ a }\n")]
    [InlineData("{ a }", " { a }")]
    [InlineData("{ a }", "{ a }\r")]
    public void ExactHash_WhitespaceChangesTheHash(string trusted, string variant) =>
        TrustedDocumentHash.ComputeExact(variant).ShouldNotBe(TrustedDocumentHash.ComputeExact(trusted));

    [Fact]
    public void HashNormalize_AcceptsOnly64Hex_AndFoldsCase()
    {
        TrustedDocumentHash.TryNormalize(new string('A', 64), out var lower).ShouldBeTrue();
        lower.ShouldBe(new string('a', 64));
        TrustedDocumentHash.TryNormalize(new string('a', 63), out _).ShouldBeFalse();
        TrustedDocumentHash.TryNormalize(new string('a', 65), out _).ShouldBeFalse();
        TrustedDocumentHash.TryNormalize(new string('g', 64), out _).ShouldBeFalse();
        TrustedDocumentHash.TryNormalize(null, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("shop/1.0.0", "shop", "1.0.0")]
    [InlineData("a/B", "a", "B")]
    [InlineData("my-app_1.x/2024.10+build-5", "my-app_1.x", "2024.10+build-5")]
    public void Header_Valid_Parses(string raw, string appId, string version)
    {
        AppIdentityHeader.TryParse(new StringValues(raw), out var id).ShouldBeTrue();
        id.ShouldBe(new AppIdentity(appId, version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("shop")]
    [InlineData("shop/")]
    [InlineData("/1.0")]
    [InlineData("shop/1/2")]
    [InlineData("Shop/1")]
    [InlineData("shop/-1")]
    [InlineData("-shop/1")]
    [InlineData("shop/1 0")]
    [InlineData("sh op/1")]
    [InlineData("shop/1\n")]
    [InlineData("shop/1;x")]
    [InlineData("shop/ü")]
    public void Header_Malformed_Fails(string raw) =>
        AppIdentityHeader.TryParse(new StringValues(raw), out _).ShouldBeFalse();

    [Fact]
    public void Header_Oversized_MultiValue_Malformed_Rejected_Before_Regex()
    {
        AppIdentityHeader.TryParse(new StringValues(new string('a', 64) + "/" + new string('1', 64)), out _).ShouldBeTrue();   // 129 chars
        AppIdentityHeader.TryParse(new StringValues(new string('a', 64) + "/" + new string('1', 65)), out _).ShouldBeFalse();
        AppIdentityHeader.TryParse(new StringValues(new string('a', 65) + "/1"), out _).ShouldBeFalse();
        AppIdentityHeader.TryParse(new StringValues(new string('a', 100_000)), out _).ShouldBeFalse();
    }

    [Fact]
    public void Header_MissingOrMultipleValues_Fail()
    {
        AppIdentityHeader.TryParse(StringValues.Empty, out _).ShouldBeFalse();
        AppIdentityHeader.TryParse(new StringValues(["shop/1", "shop/1"]), out _).ShouldBeFalse();
        AppIdentityHeader.TryParse(new StringValues(["shop/1", "other/1"]), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("HTTPS://App.Example.COM", "https://app.example.com")]
    [InlineData("https://app.example.com:443", "https://app.example.com")]
    [InlineData("http://app.example.com:80", "http://app.example.com")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("https://app.example.com/", "https://app.example.com")]
    [InlineData("https://app.example.com:8443", "https://app.example.com:8443")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080")]
    public void Origin_Normalizes(string input, string expected)
    {
        AllowedOrigin.TryNormalize(input, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("app.example.com")]
    [InlineData("ftp://app.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?x=1")]
    [InlineData("https://app.example.com#f")]
    [InlineData("https://app.example.com ")]
    [InlineData("https://app.example.com\\evil.com")]
    [InlineData("https://")]
    public void Origin_Invalid_Fails(string input) =>
        AllowedOrigin.TryNormalize(input, out _).ShouldBeFalse();

    [Fact]
    public void Origin_Oversized_Fails() =>
        AllowedOrigin.TryNormalize("https://" + new string('a', 300) + ".com", out _).ShouldBeFalse();
}
