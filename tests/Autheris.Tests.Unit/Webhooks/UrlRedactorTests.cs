namespace Autheris.Tests.Unit.Webhooks;

using Autheris.Application.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class UrlRedactorTests
{
    [Theory]
    [InlineData("https://user:pw@example.com/hook/abc?token=XYZ#frag", "https://example.com/")]
    [InlineData("https://example.com:8443/a/b?sig=1", "https://example.com:8443/")]
    [InlineData("http://example.com:80/x", "http://example.com/")]
    [InlineData("https://[2001:db8::1]:9000/p?q=1", "https://[2001:db8::1]:9000/")]
    public void Redact_KeepsOnlySchemeHostPort(string url, string expected)
    {
        UrlRedactor.Redact(url).ShouldBe(expected);
        UrlRedactor.Redact(new System.Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url?token=SECRET")]
    public void Redact_Unparsable_DoesNotEchoInput(string? url)
    {
        UrlRedactor.Redact(url).ShouldBe(UrlRedactor.Invalid);
    }
}
