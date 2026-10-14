namespace Autheris.Tests.Unit.Webhooks;

using System;
using Autheris.Application.Webhooks;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebhookSignatureTests
{
    private const string Secret = "s3cret";
    private const string Body = "{\"a\":1}";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sign_CoversTimestampAndBody()
    {
        var ts = WebhookSignature.FormatTimestamp(Now);
        WebhookSignature.Sign(Secret, ts, Body).ShouldBe(WebhookSignature.ComputeHex(Secret, $"{ts}.{Body}"));
        WebhookSignature.Sign(Secret, ts, Body).ShouldNotBe(WebhookSignature.Sign(Secret, WebhookSignature.FormatTimestamp(Now.AddSeconds(1)), Body));
    }

    [Fact]
    public void TryVerify_FreshSignature_ReturnsTrue()
    {
        var ts = WebhookSignature.FormatTimestamp(Now);
        var sig = WebhookSignature.SignHeaderValue(Secret, ts, Body);
        WebhookSignature.TryVerify(Secret, ts, Body, sig, Now.AddMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void TryVerify_OlderThanWindow_ReturnsFalse()
    {
        var ts = WebhookSignature.FormatTimestamp(Now);
        var sig = WebhookSignature.SignHeaderValue(Secret, ts, Body);
        WebhookSignature.TryVerify(Secret, ts, Body, sig, Now.AddMinutes(6)).ShouldBeFalse();
    }

    [Fact]
    public void TryVerify_FutureBeyondWindow_ReturnsFalse()
    {
        var ts = WebhookSignature.FormatTimestamp(Now.AddMinutes(10));
        var sig = WebhookSignature.SignHeaderValue(Secret, ts, Body);
        WebhookSignature.TryVerify(Secret, ts, Body, sig, Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "x", "sha256=ab")]
    [InlineData("s", null, "sha256=ab")]
    [InlineData("s", "not-a-date", "sha256=ab")]
    [InlineData("s", "2026-10-14T12:00:00.0000000+00:00", null)]
    [InlineData("s", "2026-10-14T12:00:00.0000000+00:00", "ab")]
    [InlineData("s", "2026-10-14T12:00:00.0000000+00:00", "sha256=zz")]
    public void TryVerify_MalformedInput_ReturnsFalseWithoutThrowing(string? secret, string? ts, string? sig)
    {
        WebhookSignature.TryVerify(secret, ts, Body, sig, Now).ShouldBeFalse();
    }

    [Fact]
    public void TryVerify_TamperedBodyOrSecret_ReturnsFalse()
    {
        var ts = WebhookSignature.FormatTimestamp(Now);
        var sig = WebhookSignature.SignHeaderValue(Secret, ts, Body);
        WebhookSignature.TryVerify(Secret, ts, Body + " ", sig, Now).ShouldBeFalse();
        WebhookSignature.TryVerify("other", ts, Body, sig, Now).ShouldBeFalse();
    }
}
