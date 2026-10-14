namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebhookDeliveryClientTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static WebhookOutboxMessage Message(string eventId = "evt-1", string? tenant = "tenant-A") => new(
        Id: "row-1",
        Source: WebhookSubscriptionSource.Configured,
        SubscriptionId: "sub-1",
        OwnerTenantId: tenant,
        EventTenantId: tenant,
        EventId: eventId,
        EventType: "autheris.cdc.insert",
        EventSource: "/autheris/cdc/t",
        EventTime: DateTimeOffset.Parse("2026-10-04T12:00:00Z"),
        PayloadJson: "{\"id\":1}",
        Attempt: 1,
        MaxAttempts: 5,
        State: WebhookOutboxState.InFlight,
        NextAttemptAt: Start,
        LeaseUntil: Start.AddMinutes(1),
        LeaseOwner: "w",
        LastStatusCode: null,
        LastErrorCode: null,
        CreatedAt: Start);

    private static ResolvedWebhookTarget Target(
        string url = "https://example.com/events/v1",
        string? secret = "secret-1",
        IReadOnlyDictionary<string, string>? headers = null) => new(url, secret, headers, true);

    private sealed class Fixture
    {
        public WebhookCapturingHandler Handler { get; } = new();
        public WebhookCapturingLogger<WebhookDeliveryClient> Logger { get; } = new();
        public WebhookManualTime Clock { get; } = new(Start);
        public WebhookDeliveryClient Client { get; }

        public Fixture(string environment = "Production", bool? requireSecret = null, Func<Uri, bool, CancellationToken, Task>? validator = null, int timeoutSeconds = 5)
        {
            var options = new GatewayOptions { Webhooks = new WebhooksOptions { RequireSecret = requireSecret, DefaultTimeoutSeconds = timeoutSeconds } };
            var env = Substitute.For<IHostEnvironment>();
            env.EnvironmentName.Returns(environment);
            Client = new WebhookDeliveryClient(
                new HttpClient(Handler),
                Options.Create(options),
                new WebhookRetryPolicy(options.Webhooks, Clock, () => 1.0),
                Logger,
                env,
                Clock,
                validator ?? ((_, _, _) => Task.CompletedTask));
        }
    }

    [Fact]
    public async Task Delivery_Retry_RecomputesTimestampAndSignature()
    {
        var f = new Fixture();

        await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        f.Handler.Calls.Count.ShouldBe(2);
        var ts = f.Handler.Calls.Select(c => c.Request.Headers.GetValues("X-Autheris-Timestamp").Single()).ToList();
        var sig = f.Handler.Calls.Select(c => c.Request.Headers.GetValues("X-Autheris-Signature-256").Single()).ToList();
        ts[0].ShouldNotBe(ts[1]);
        sig[0].ShouldNotBe(sig[1]);
        DateTimeOffset.Parse(ts[1], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Start.AddMinutes(3));
        WebhookSignature.TryVerify("secret-1", ts[1], f.Handler.Calls[1].Body, sig[1], Start.AddMinutes(3)).ShouldBeTrue();
        // The first signature would be a replay by now.
        WebhookSignature.TryVerify("secret-1", ts[0], f.Handler.Calls[0].Body, sig[0], Start.AddMinutes(3).AddMinutes(6)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delivery_DisposesResponse()
    {
        var f = new Fixture();
        var content = new TrackingContent();
        f.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        content.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Delivery_UsesEnvironmentIsDev()
    {
        Func<Uri, bool, CancellationToken, Task> real = Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync;
        var dev = new Fixture("Development", validator: real);
        var prod = new Fixture("Production", validator: real);

        var devResult = await dev.Client.DeliverAsync(Message(), Target("http://10.9.8.7/hook"), CancellationToken.None);
        var prodResult = await prod.Client.DeliverAsync(Message(), Target("http://10.9.8.7/hook"), CancellationToken.None);

        devResult.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Success);
        dev.Handler.Calls.Count.ShouldBe(1);
        prodResult.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        prodResult.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        prod.Handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_RevalidatesEgressOnEveryAttempt()
    {
        var calls = 0;
        var f = new Fixture(validator: (_, _, _) =>
        {
            calls++;
            if (calls >= 2)
            {
                // DNS now points at a private address.
                var ex = new SecurityException("Outbound access to a private/loopback/restricted address is strictly forbidden.");
                ex.Data["ResolvedIp"] = "10.0.0.5";
                throw ex;
            }

            return Task.CompletedTask;
        });

        var first = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);
        var second = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        first.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Success);
        second.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        second.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        f.Handler.Calls.Count.ShouldBe(1); // the second attempt never reached the network
    }

    [Fact]
    public async Task Delivery_ErrorMessage_ContainsNoResolvedIp()
    {
        var f = new Fixture(validator: (_, _, _) =>
        {
            var ex = new SecurityException("blocked 10.20.30.40 internal detail");
            ex.Data["ResolvedIp"] = "10.20.30.40";
            throw ex;
        });

        var result = await f.Client.DeliverAsync(Message(), Target("https://example.com/path?token=TOPSECRET"), CancellationToken.None);

        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        result.Decision.ErrorCode!.ShouldNotContain("10.20.30.40");
        var log = f.Logger.All;
        log.ShouldNotContain("10.20.30.40");
        log.ShouldNotContain("internal detail");
        log.ShouldNotContain("TOPSECRET");
        log.ShouldContain("https://example.com/");
    }

    [Fact]
    public async Task Delivery_NetworkFailure_LogsOnlyTypeAndRedactedUrl()
    {
        var f = new Fixture();
        f.Handler.Respond = _ => throw new HttpRequestException("connect to 192.168.1.9 failed");

        var result = await f.Client.DeliverAsync(Message(), Target("https://example.com/p?token=TOPSECRET"), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.Network);
        f.Logger.All.ShouldNotContain("192.168.1.9");
        f.Logger.All.ShouldNotContain("TOPSECRET");
        f.Logger.All.ShouldContain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task Delivery_SignsTimestampAndPayload_ReplayedWithOldTimestampFailsVerification()
    {
        var f = new Fixture();

        var result = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Success);
        result.StatusCode.ShouldBe(200);
        var (req, body) = f.Handler.Calls.Single();
        var ts = req.Headers.GetValues("X-Autheris-Timestamp").Single();
        var sig = req.Headers.GetValues("X-Autheris-Signature-256").Single();
        req.Headers.GetValues("X-Autheris-Event-Id").Single().ShouldBe("evt-1");
        req.Headers.GetValues("X-Autheris-Event-Type").Single().ShouldBe("autheris.cdc.insert");
        WebhookSignature.TryVerify("secret-1", ts, body, sig, Start.AddSeconds(30)).ShouldBeTrue();
        WebhookSignature.TryVerify("secret-1", ts, body, sig, Start.AddMinutes(6)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delivery_SendsCloudEventHeadersAndBody()
    {
        var f = new Fixture();

        await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        var (req, body) = f.Handler.Calls.Single();
        req.Method.ShouldBe(HttpMethod.Post);
        req.Headers.GetValues("ce-specversion").Single().ShouldBe("1.0");
        req.Headers.GetValues("ce-id").Single().ShouldBe("evt-1");
        req.Headers.GetValues("ce-type").Single().ShouldBe("autheris.cdc.insert");
        req.Headers.GetValues("ce-source").Single().ShouldBe("/autheris/cdc/t");
        req.Headers.GetValues("ce-tenantid").Single().ShouldBe("tenant-A");
        req.Headers.GetValues("ce-time").Single().ShouldBe(Message().EventTime.ToString("O"));
        req.Content!.Headers.ContentType!.MediaType.ShouldBe("application/cloudevents+json");
        body.ShouldBe("{\"id\":1}");
    }

    [Fact]
    public async Task Delivery_NeverEmitsLegacySignatureHeader()
    {
        var f = new Fixture();

        await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        var req = f.Handler.Calls.Single().Request;
        req.Headers.Contains("X-Autheris-Signature").ShouldBeFalse();
        req.Headers.Contains("X-Autheris-Signature-256").ShouldBeTrue();
    }

    [Fact]
    public async Task Delivery_CustomHeaders_AreAppliedExceptForbiddenOnes()
    {
        var f = new Fixture();
        var headers = new Dictionary<string, string>
        {
            ["X-Api-Key"] = "k-1",
            ["X-Autheris-Signature"] = "forged",
            ["Host"] = "evil.example",
            ["ce-id"] = "forged"
        };

        await f.Client.DeliverAsync(Message(), Target(headers: headers), CancellationToken.None);

        var req = f.Handler.Calls.Single().Request;
        req.Headers.GetValues("X-Api-Key").Single().ShouldBe("k-1");
        req.Headers.Contains("X-Autheris-Signature").ShouldBeFalse();
        req.Headers.GetValues("ce-id").Single().ShouldBe("evt-1");
        req.Headers.Host.ShouldBeNull();
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("ce-id")]
    [InlineData("X-Autheris-Signature")]
    [InlineData("X-Autheris-Timestamp")]
    [InlineData("x-autheris-signature-256")]
    [InlineData("X-Autheris-Event-Id")]
    public void ForbiddenCustomHeaders_AreForbidden(string name) =>
        WebhookDeliveryClient.IsForbiddenCustomHeader(name).ShouldBeTrue();

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("Authorization")]
    [InlineData("X-Custom-Tenant")]
    public void AllowedCustomHeaders_AreAllowed(string name) =>
        WebhookDeliveryClient.IsForbiddenCustomHeader(name).ShouldBeFalse();

    [Fact]
    public void ForbiddenCustomHeaders_StillBlockLegacySignatureName() =>
        WebhookDeliveryClient.IsForbiddenCustomHeader("X-Autheris-Signature").ShouldBeTrue();

    [Fact]
    public async Task RequireSecret_MissingAtDelivery_DeadLettersWithoutSending()
    {
        var f = new Fixture(requireSecret: true);

        var result = await f.Client.DeliverAsync(Message(), Target(secret: null), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SecretMissing);
        f.Handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptySecret_InDevelopment_SendsUnsignedButTimestamped()
    {
        var f = new Fixture("Development", requireSecret: null);

        var result = await f.Client.DeliverAsync(Message(), Target(secret: string.Empty), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Success);
        var req = f.Handler.Calls.Single().Request;
        req.Headers.Contains("X-Autheris-Signature-256").ShouldBeFalse();
        req.Headers.Contains("X-Autheris-Timestamp").ShouldBeTrue();
    }

    [Fact]
    public async Task EmptySecret_OutsideDevelopment_IsRequiredByDefault()
    {
        var f = new Fixture("Production", requireSecret: null);

        var result = await f.Client.DeliverAsync(Message(), Target(secret: string.Empty), CancellationToken.None);

        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SecretMissing);
        f.Handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_SsrfRejection_LogsRedactedUrlAndNoMessage()
    {
        var f = new Fixture(validator: (u, dev, ct) => Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(u, dev, ct));

        var result = await f.Client.DeliverAsync(Message(), Target("https://10.9.8.7/secret/path?token=TOPSECRET"), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        f.Handler.Calls.ShouldBeEmpty();
        var log = f.Logger.All;
        log.ShouldNotContain("TOPSECRET");
        log.ShouldNotContain("/secret/path");
        log.ShouldContain("https://10.9.8.7/");
        log.ShouldNotContain("forbidden");
    }

    [Fact]
    public async Task Delivery_MetadataEndpoint_IsBlockedWithoutSending()
    {
        var f = new Fixture(validator: (u, dev, ct) => Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(u, dev, ct));

        var result = await f.Client.DeliverAsync(Message(), Target("http://169.254.169.254/latest/meta-data/"), CancellationToken.None);

        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        f.Handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_InvalidUrl_IsPermanent()
    {
        var f = new Fixture();

        var result = await f.Client.DeliverAsync(Message(), Target("not a url"), CancellationToken.None);

        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.InvalidUrl);
        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
    }

    [Theory]
    [InlineData(404, WebhookRetryOutcome.Permanent)]
    [InlineData(302, WebhookRetryOutcome.Permanent)]
    [InlineData(500, WebhookRetryOutcome.Retry)]
    [InlineData(429, WebhookRetryOutcome.Retry)]
    public async Task Delivery_ClassifiesHttpStatus(int status, WebhookRetryOutcome expected)
    {
        var f = new Fixture();
        f.Handler.Respond = _ => new HttpResponseMessage((HttpStatusCode)status);

        var result = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(expected);
        result.StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task Delivery_429WithRetryAfter_CarriesRetryAfter()
    {
        var f = new Fixture();
        f.Handler.Respond = _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        };

        var result = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        result.Decision.RetryAfter.ShouldBe(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task Delivery_ConnectTimeSecurityException_IsEgressDenied()
    {
        var f = new Fixture();
        f.Handler.Respond = _ => throw new HttpRequestException("x", new SecurityException("rebinding"));

        var result = await f.Client.DeliverAsync(Message(), Target(), CancellationToken.None);

        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
    }

    [Fact]
    public async Task Delivery_Shutdown_PropagatesCancellationAsRetryWithoutCode()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Handler.Respond = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var result = await f.Client.DeliverAsync(Message(), Target(), cts.Token);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Retry);
        result.Decision.ErrorCode.ShouldBeNull();
    }
}
