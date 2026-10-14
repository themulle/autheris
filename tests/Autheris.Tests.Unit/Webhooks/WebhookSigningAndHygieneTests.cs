namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebhookSigningAndHygieneTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }

    private static CloudEventEnvelope Envelope() => new(
        SpecVersion: "1.0",
        Id: "evt-1",
        Source: "/autheris/cdc/t",
        Type: "autheris.cdc.insert",
        Time: DateTimeOffset.Parse("2026-10-04T12:00:00Z"),
        DataContentType: "application/json",
        TenantId: "tenant-A",
        Data: new Dictionary<string, object?> { ["id"] = 1 });

    private static CloudEventWebhookSubscription Subscription(string url = "https://example.com/events/v1", string secret = "secret-1") => new(
        Id: "sub",
        TenantId: "tenant-A",
        TargetUrl: url,
        FilterTable: "*",
        FilterOperations: [CdcOperation.Insert],
        HmacSecret: secret);

    [Fact]
    public async Task Dispatch_SignsTimestampAndPayload_ReplayedWithOldTimestampFailsVerification()
    {
        var handler = new CapturingHandler();
        var dispatcher = new CloudEventWebhookDispatcher(new HttpClient(handler), new CapturingLogger<CloudEventWebhookDispatcher>());

        var result = await dispatcher.DispatchAsync(Subscription(), Envelope());

        result.Success.ShouldBeTrue();
        var (req, body) = handler.Calls.Single();
        var ts = req.Headers.GetValues("X-Autheris-Timestamp").Single();
        var sig = req.Headers.GetValues("X-Autheris-Signature-256").Single();
        req.Headers.GetValues("X-Autheris-Event-Id").Single().ShouldBe("evt-1");
        req.Headers.GetValues("X-Autheris-Event-Type").Single().ShouldBe("autheris.cdc.insert");

        var sentAt = DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture);
        WebhookSignature.TryVerify("secret-1", ts, body, sig, sentAt.AddSeconds(30)).ShouldBeTrue();
        WebhookSignature.TryVerify("secret-1", ts, body, sig, sentAt.AddMinutes(6)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delivery_NeverEmitsLegacySignatureHeader()
    {
        var handler = new CapturingHandler();
        await new CloudEventWebhookDispatcher(new HttpClient(handler), new CapturingLogger<CloudEventWebhookDispatcher>())
            .DispatchAsync(Subscription(), Envelope());
        var req = handler.Calls.Single().Request;
        req.Headers.Contains("X-Autheris-Signature").ShouldBeFalse();
        req.Headers.Contains("X-Autheris-Signature-256").ShouldBeTrue();
    }

    [Fact]
    public void ForbiddenCustomHeaders_StillBlockLegacySignatureName() =>
        CloudEventWebhookDispatcher.IsForbiddenCustomHeader("X-Autheris-Signature").ShouldBeTrue();

    [Theory]
    [InlineData("X-Autheris-Timestamp")]
    [InlineData("x-autheris-signature-256")]
    [InlineData("X-Autheris-Event-Id")]
    public void CustomHeaders_SigningHeaders_AreForbidden(string name) =>
        CloudEventWebhookDispatcher.IsForbiddenCustomHeader(name).ShouldBeTrue();

    [Fact]
    public async Task Dispatch_SsrfRejection_LogsRedactedUrlAndNoMessage()
    {
        var handler = new CapturingHandler();
        var logger = new CapturingLogger<CloudEventWebhookDispatcher>();
        var dispatcher = new CloudEventWebhookDispatcher(new HttpClient(handler), logger);

        var result = await dispatcher.DispatchAsync(Subscription("https://10.9.8.7/secret/path?token=TOPSECRET"), Envelope());

        result.Success.ShouldBeFalse();
        handler.Calls.ShouldBeEmpty();
        var log = string.Join("\n", logger.Messages);
        log.ShouldNotContain("TOPSECRET");
        log.ShouldNotContain("/secret/path");
        // the redacted URL keeps scheme and host only; the egress policy message itself is never logged
        log.ShouldContain("https://10.9.8.7/");
        log.ShouldNotContain("forbidden");
    }

    private static (WebhookDispatcherService Dispatcher, CapturingHandler Handler, CapturingLogger<WebhookDispatcherService> Logger) CreateService(
        string secret, bool? requireSecret, string url = "https://receiver.internal/webhook?token=TOPSECRET")
    {
        var handler = new CapturingHandler();
        var logger = new CapturingLogger<WebhookDispatcherService>();
        var options = Options.Create(new GatewayOptions
        {
            Webhooks = new WebhooksOptions
            {
                RequireSecret = requireSecret,
                Subscriptions =
                [
                    new WebhookSubscription { Id = "s1", TargetUrl = url, Secret = secret, IsClusterWide = true, MaxAttempts = 1 }
                ]
            }
        });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return (new WebhookDispatcherService(new HttpClient(handler), options, logger, env), handler, logger);
    }

    private static AccessPlanCreatedEvent Event() =>
        new(EventId: "ev-1", PlanId: "p", DatasetId: "d", RequesterSid: "u", GrantCount: 1, Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public async Task EmptySecret_DoesNotSendSignatureHeader()
    {
        var (dispatcher, handler, _) = CreateService(secret: string.Empty, requireSecret: null);

        await dispatcher.DispatchEventAsync(Event());

        var req = handler.Calls.Single().Request;
        req.Headers.Contains("X-Autheris-Signature-256").ShouldBeFalse();
        req.Headers.Contains("X-Autheris-Timestamp").ShouldBeTrue();
    }

    [Fact]
    public async Task RequireSecret_EmptySecret_DropsDelivery()
    {
        var (dispatcher, handler, logger) = CreateService(secret: string.Empty, requireSecret: true);

        await dispatcher.DispatchEventAsync(Event());

        handler.Calls.ShouldBeEmpty();
        string.Join("\n", logger.Messages).ShouldNotContain("TOPSECRET");
    }

    [Fact]
    public async Task Dispatch_WithSecret_SignsTimestampDotBody_AndLogsOnlyRedactedUrl()
    {
        var (dispatcher, handler, logger) = CreateService(secret: "k", requireSecret: true);

        await dispatcher.DispatchEventAsync(Event());

        var (req, body) = handler.Calls.Single();
        var ts = req.Headers.GetValues("X-Autheris-Timestamp").Single();
        req.Headers.GetValues("X-Autheris-Signature-256").Single()
            .ShouldBe(WebhookSignature.SignHeaderValue("k", ts, body));
        string.Join("\n", logger.Messages).ShouldNotContain("TOPSECRET");
    }
}
