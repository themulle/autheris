namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CloudEventWebhookSecurityTests
{
    private readonly ICloudEventTransformer _transformer = new CloudEventTransformer();

    [Fact]
    public void Transform_GeneratesCompliantCloudEventsV1Envelope()
    {
        // Arrange
        var cdcEvent = new CdcEvent(
            EventId: "evt-12345",
            Table: new TableIdentifier("sales", "public", "orders"),
            Operation: CdcOperation.Insert,
            TenantId: "tenant-acme",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 42, ["amount"] = 199.99 },
            Timestamp: DateTimeOffset.Parse("2026-10-04T10:00:00Z")
        );

        var maskedData = new Dictionary<string, object?> { ["id"] = 42, ["amount"] = "[MASKED]" };

        // Act
        var envelope = _transformer.Transform(cdcEvent, maskedData);

        // Assert
        envelope.SpecVersion.ShouldBe("1.0");
        envelope.Id.ShouldBe("evt-12345");
        envelope.Source.ShouldBe("/autheris/cdc/sales/public/orders");
        envelope.Type.ShouldBe("autheris.cdc.insert");
        envelope.TenantId.ShouldBe("tenant-acme");
        envelope.DataContentType.ShouldBe("application/json");
        envelope.Data.ShouldBe(maskedData);
    }

    [Fact]
    public async Task SubscriptionStore_EnforcesStrictTenantAndOperationIsolation()
    {
        // Arrange
        var store = new InMemoryCloudEventSubscriptionStore();

        var subTenantA = new CloudEventWebhookSubscription(
            Id: "sub-1",
            TenantId: "tenant-A",
            TargetUrl: "https://api.partner.com/webhook",
            FilterTable: "orders",
            FilterOperations: [CdcOperation.Insert],
            HmacSecret: "secret-a"
        );

        var subTenantB = new CloudEventWebhookSubscription(
            Id: "sub-2",
            TenantId: "tenant-B",
            TargetUrl: "https://api.tenant-b.com/webhook",
            FilterTable: "orders",
            FilterOperations: [CdcOperation.Insert, CdcOperation.Update],
            HmacSecret: "secret-b"
        );

        await store.RegisterSubscriptionAsync(subTenantA);
        await store.RegisterSubscriptionAsync(subTenantB);

        // Act 1: Query for Tenant-A Insert on orders
        var matchedA = await store.GetSubscriptionsAsync("tenant-A", "orders", CdcOperation.Insert);
        matchedA.Count.ShouldBe(1);
        matchedA.ShouldContain(s => s.Id == "sub-1");

        // Act 2: Query for Tenant-A Update on orders (not subscribed)
        var matchedAUpdate = await store.GetSubscriptionsAsync("tenant-A", "orders", CdcOperation.Update);
        matchedAUpdate.ShouldBeEmpty();

        // Act 3: Query for Tenant-B (must never return Tenant-A subscriptions)
        var matchedB = await store.GetSubscriptionsAsync("tenant-B", "orders", CdcOperation.Insert);
        matchedB.Count.ShouldBe(1);
        matchedB.ShouldContain(s => s.Id == "sub-2");
        matchedB.ShouldNotContain(s => s.TenantId == "tenant-A");
    }

    private static CloudEventEnvelope Envelope(string id = "evt-update-99") => new(
        SpecVersion: "1.0",
        Id: id,
        Source: "/autheris/cdc/customers",
        Type: "autheris.cdc.update",
        Time: DateTimeOffset.Parse("2026-10-04T12:00:00Z"),
        DataContentType: "application/json",
        TenantId: "tenant-A",
        Data: new Dictionary<string, object?> { ["id"] = "C-1", ["name"] = "Alice" });

    private static CloudEventWebhookSubscription Subscription(string url, string secret = "super-secret-key-12345") => new(
        Id: "sub-valid",
        TenantId: "tenant-A",
        TargetUrl: url,
        FilterTable: "customers",
        FilterOperations: [CdcOperation.Update],
        HmacSecret: secret);

    private static (WebhookDeliveryClient Client, Capturing Handler) NewClient(bool realEgressPolicy)
    {
        var handler = new Capturing();
        var options = Options.Create(new GatewayOptions());
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var client = new WebhookDeliveryClient(
            new HttpClient(handler), options, new WebhookRetryPolicy(options.Value.Webhooks, TimeProvider.System),
            NullLogger<WebhookDeliveryClient>.Instance, env, null,
            realEgressPolicy ? (u, dev, ct) => Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(u, dev, ct) : (_, _, _) => Task.CompletedTask);
        return (client, handler);
    }

    private sealed class Capturing : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task CloudEventDelivery_WithSsrfAttemptToLoopbackOrMetadata_BlocksDelivery()
    {
        var (client, handler) = NewClient(realEgressPolicy: true);
        var subscription = Subscription("http://169.254.169.254/latest/meta-data/");
        var row = CloudEventOutboxMessageFactory.Create(subscription, Envelope(), DateTimeOffset.UtcNow, 5);

        var result = await client.DeliverAsync(row, new ResolvedWebhookTarget(subscription.TargetUrl, subscription.HmacSecret, null, true), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Permanent);
        result.Decision.ErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        handler.Calls.ShouldBeEmpty(); // no HTTP call was dispatched
    }

    [Fact]
    public async Task CloudEventDelivery_ComputesHmacSha256SignatureAndCloudEventsHeaders()
    {
        const string hmacSecret = "super-secret-key-12345";
        var (client, handler) = NewClient(realEgressPolicy: false);
        var subscription = Subscription("https://example.com/events/v1", hmacSecret);
        var row = CloudEventOutboxMessageFactory.Create(subscription, Envelope(), DateTimeOffset.UtcNow, 5);

        var result = await client.DeliverAsync(row, new ResolvedWebhookTarget(subscription.TargetUrl, hmacSecret, null, true), CancellationToken.None);

        result.Decision.Outcome.ShouldBe(WebhookRetryOutcome.Success);
        var (request, body) = handler.Calls.Single();
        request.Headers.GetValues("ce-specversion").ShouldContain("1.0");
        request.Headers.GetValues("ce-id").ShouldContain("evt-update-99");
        request.Headers.GetValues("ce-type").ShouldContain("autheris.cdc.update");
        request.Headers.Contains("X-Autheris-Signature").ShouldBeFalse();
        var sentSignature = request.Headers.GetValues("X-Autheris-Signature-256").Single();
        var timestamp = request.Headers.GetValues("X-Autheris-Timestamp").Single();
        sentSignature.ShouldBe(WebhookSignature.SignHeaderValue(hmacSecret, timestamp, body));
        body.ShouldBe(row.PayloadJson);
    }

    [Fact]
    public void Factory_BuildsDurableRow_WithoutSecretUrlOrHeaders()
    {
        var subscription = Subscription("https://example.com/events/v1?token=SECRETTOKEN") with
        {
            CustomHeaders = new Dictionary<string, string> { ["X-Api-Key"] = "HEADERVALUE" }
        };
        var now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

        var row = CloudEventOutboxMessageFactory.Create(subscription, Envelope(), now, 99);

        row.Source.ShouldBe(WebhookSubscriptionSource.CloudEvent);
        row.SubscriptionId.ShouldBe("sub-valid");
        row.OwnerTenantId.ShouldBe("tenant-A");
        row.EventTenantId.ShouldBe("tenant-A");
        row.EventId.ShouldBe("evt-update-99");
        row.State.ShouldBe(WebhookOutboxState.Pending);
        row.NextAttemptAt.ShouldBe(now);
        row.MaxAttempts.ShouldBe(WebhookSubscription.MaxAllowedAttempts);
        var all = JsonSerializer.Serialize(row);
        all.ShouldNotContain("super-secret-key-12345");
        all.ShouldNotContain("SECRETTOKEN");
        all.ShouldNotContain("HEADERVALUE");
        row.PayloadJson.ShouldContain("\"specversion\"");
    }

    [Fact]
    public async Task SubscriptionStore_GetSubscription_IsTenantScoped()
    {
        var store = new InMemoryCloudEventSubscriptionStore();
        await store.RegisterSubscriptionAsync(Subscription("https://example.com/a"));

        (await store.GetSubscriptionAsync("tenant-A", "sub-valid")).ShouldNotBeNull();
        (await store.GetSubscriptionAsync("tenant-B", "sub-valid")).ShouldBeNull();
        (await store.GetSubscriptionAsync("tenant-A", "unknown")).ShouldBeNull();
    }
}
