namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task Dispatcher_WithSsrfAttemptToLoopbackOrMetadata_BlocksDelivery()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((req) => new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler);
        var dispatcher = new CloudEventWebhookDispatcher(httpClient, NullLogger<CloudEventWebhookDispatcher>.Instance);

        var maliciousSubscription = new CloudEventWebhookSubscription(
            Id: "sub-ssrf",
            TenantId: "tenant-A",
            TargetUrl: "http://169.254.169.254/latest/meta-data/", // AWS metadata SSRF attempt
            FilterTable: "*",
            FilterOperations: [CdcOperation.Insert],
            HmacSecret: "secret"
        );

        var envelope = new CloudEventEnvelope(
            SpecVersion: "1.0",
            Id: "evt-1",
            Source: "/autheris/cdc/orders",
            Type: "autheris.cdc.insert",
            Time: DateTimeOffset.UtcNow,
            DataContentType: "application/json",
            TenantId: "tenant-A",
            Data: new { id = 1 }
        );

        // Act
        var result = await dispatcher.DispatchAsync(maliciousSubscription, envelope);

        // Assert
        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("SSRF");
        handler.CallCount.ShouldBe(0); // Ensure no HTTP call was dispatched!
    }

    [Fact]
    public async Task Dispatcher_ComputesHmacSha256SignatureAndCloudEventsHeaders()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(async (req) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpClient = new HttpClient(handler);
        var dispatcher = new CloudEventWebhookDispatcher(httpClient, NullLogger<CloudEventWebhookDispatcher>.Instance);

        const string hmacSecret = "super-secret-key-12345";
        var subscription = new CloudEventWebhookSubscription(
            Id: "sub-valid",
            TenantId: "tenant-A",
            TargetUrl: "https://api.partner.com/events/v1",
            FilterTable: "customers",
            FilterOperations: [CdcOperation.Update],
            HmacSecret: hmacSecret
        );

        var envelope = new CloudEventEnvelope(
            SpecVersion: "1.0",
            Id: "evt-update-99",
            Source: "/autheris/cdc/customers",
            Type: "autheris.cdc.update",
            Time: DateTimeOffset.Parse("2026-10-04T12:00:00Z"),
            DataContentType: "application/json",
            TenantId: "tenant-A",
            Data: new Dictionary<string, object?> { ["id"] = "C-1", ["name"] = "Alice" }
        );

        // Act
        var result = await dispatcher.DispatchAsync(subscription, envelope);

        // Assert
        result.Success.ShouldBeTrue();
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Headers.GetValues("ce-specversion").ShouldContain("1.0");
        capturedRequest.Headers.GetValues("ce-id").ShouldContain("evt-update-99");
        capturedRequest.Headers.GetValues("ce-type").ShouldContain("autheris.cdc.update");

        // Verify HMAC-SHA256 signature
        capturedRequest.Headers.Contains("X-Autheris-Signature").ShouldBeTrue();
        var sentSignature = string.Join("", capturedRequest.Headers.GetValues("X-Autheris-Signature"));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hmacSecret));
        var expectedHashHex = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(capturedBody!)));
        sentSignature.ShouldBe($"sha256={expectedHashHex}");
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _syncFunc;
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _asyncFunc;
        public int CallCount { get; private set; }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> func)
        {
            _syncFunc = func;
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncFunc)
        {
            _asyncFunc = asyncFunc;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_asyncFunc != null)
            {
                return await _asyncFunc(request);
            }
            return _syncFunc!(request);
        }
    }
}
