namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Authentication;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class WebhookRetryPolicyTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static WebhookRetryPolicy Policy(WebhooksOptions? options = null, double jitter = 1.0) =>
        new(options ?? new WebhooksOptions(), new FixedTime(DateTimeOffset.Parse("2026-10-14T12:00:00Z")), () => jitter);

    [Fact]
    public void Classify_Http404_IsPermanent_AndNotRetried()
    {
        var d = Policy().Classify(404, null);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.HttpStatus);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    public void Classify_2xx_IsSuccess(int status) =>
        Policy().Classify(status, null).Outcome.ShouldBe(WebhookRetryOutcome.Success);

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Classify_5xx_408_429_Retry(int status)
    {
        var d = Policy().Classify(status, null);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.HttpStatus);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public void Classify_3xx_Permanent(int status)
    {
        var d = Policy().Classify(status, null);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.HttpRedirect);
    }

    [Fact]
    public void RetryAfter_IsCappedAtMax()
    {
        var policy = Policy(new WebhooksOptions { MaxRetryAfterSeconds = 60 });
        var d = policy.Classify(429, new RetryConditionHeaderValue(TimeSpan.FromHours(10)));
        d.RetryAfter.ShouldBe(TimeSpan.FromSeconds(60));
        policy.ComputeDelay(1, TimeSpan.FromHours(10)).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void RetryAfter_Date_IsRelativeToTimeProvider()
    {
        var d = Policy().Classify(503, new RetryConditionHeaderValue(DateTimeOffset.Parse("2026-10-14T12:00:30Z")));
        d.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(1, 0.0, 0)]
    [InlineData(1, 0.5, 2.5)]
    [InlineData(3, 1.0, 20)]
    [InlineData(30, 1.0, 3600)]
    public void ComputeDelay_FullJitter_WithinBounds_AndCapped(int attempt, double jitter, double expectedSeconds)
    {
        var delay = Policy(new WebhooksOptions(), jitter).ComputeDelay(attempt, null);
        delay.TotalSeconds.ShouldBe(expectedSeconds, 0.001);
    }

    [Fact]
    public void ComputeDelay_RetryAfterWinsWhenLargerThanBackoff()
    {
        Policy(jitter: 0.0).ComputeDelay(1, TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Classify_SecurityExceptionInnerOfHttpRequest_IsEgressDenied()
    {
        var ex = new HttpRequestException("x", new SecurityException("blocked 10.0.0.1"));
        var d = Policy().Classify(ex, false);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
    }

    [Fact]
    public void Classify_SecurityException_IsEgressDenied()
    {
        var d = Policy().Classify(new SecurityException("blocked"), false);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
    }

    [Fact]
    public void Classify_DnsUnresolved_IsRetry()
    {
        var ex = new SecurityException("could not be resolved");
        ex.Data["EgressReason"] = "unresolved";
        var d = Policy().Classify(ex, false);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        d.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.DnsUnresolved);
    }

    [Fact]
    public void Classify_Timeout_Tls_Network_InvalidUrl()
    {
        var p = Policy();
        p.Classify(new TaskCanceledException(), false).ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.Timeout);
        p.Classify(new TaskCanceledException(), false).Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        p.Classify(new AuthenticationException(), false).ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.Tls);
        p.Classify(new HttpRequestException("boom"), false).ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.Network);
        var invalid = p.Classify(new UriFormatException(), false);
        invalid.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        invalid.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.InvalidUrl);
    }

    [Fact]
    public void Classify_CancellationDuringShutdown_IsRetryWithoutErrorCode()
    {
        var d = Policy().Classify(new OperationCanceledException(), true);
        d.Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        d.ErrorCode.ShouldBeNull();
    }
}
