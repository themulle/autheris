namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebhookDispatcherServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpStatusCode StatusCodeToReturn { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(request);
            return new HttpResponseMessage(StatusCodeToReturn);
        }
    }

    [Fact]
    public void ComputeHmacSha256_ProducesValidExpectedSignature()
    {
        // Arrange
        const string secret = "super-secret-key-123";
        const string payload = "test-payload-content";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        // Act
        var result = WebhookDispatcherService.ComputeHmacSha256(secret, payload);

        // Assert
        result.ShouldBe(expectedHex);
    }

    [Theory]
    [InlineData("governance.access_plan.created", "governance.*", true)]
    [InlineData("governance.access_plan.applied", "governance.*", true)]
    [InlineData("catalog.dataset.state_changed", "governance.*", false)]
    [InlineData("catalog.dataset.state_changed", "*", true)]
    [InlineData("security.alert", "security.alert", true)]
    [InlineData("security.policy.denied", "security.alert", false)]
    public void WebhookSubscription_MatchesEvent_MatchesCorrectly(string eventType, string filter, bool expectedMatch)
    {
        // Arrange
        var subscription = new WebhookSubscription
        {
            IsActive = true,
            IsClusterWide = true, // SR-M-12: tenant-less subscriptions need an explicit cluster-wide opt-in
            EventFilters = [filter]
        };

        // Act
        var isMatch = subscription.MatchesEvent(eventType, null);

        // Assert
        isMatch.ShouldBe(expectedMatch);
    }

    [Fact]
    public void WebhookSubscription_TenantFilter_IsolatesTenants()
    {
        // Arrange
        var subscription = new WebhookSubscription
        {
            IsActive = true,
            TenantId = "tenant-a",
            EventFilters = ["*"]
        };

        // Act & Assert
        subscription.MatchesEvent("test.event", "tenant-a").ShouldBeTrue();
        subscription.MatchesEvent("test.event", "tenant-b").ShouldBeFalse();
        subscription.MatchesEvent("test.event", null).ShouldBeFalse();
    }

    [Fact]
    public void SR_M_12_TenantlessSubscription_WithoutClusterWideFlag_ReceivesNothing()
    {
        var subscription = new WebhookSubscription { IsActive = true, EventFilters = ["*"] };

        subscription.MatchesEvent("security.alert", "tenant-a").ShouldBeFalse();
        subscription.MatchesEvent("security.alert", null).ShouldBeFalse();
    }

    [Fact]
    public void SR_M_12_ClusterWideSubscription_ReceivesTenantAndGlobalEvents()
    {
        var subscription = new WebhookSubscription { IsActive = true, IsClusterWide = true, EventFilters = ["*"] };

        subscription.MatchesEvent("security.alert", "tenant-a").ShouldBeTrue();
        subscription.MatchesEvent("security.alert", "tenant-b").ShouldBeTrue();
        subscription.MatchesEvent("security.alert", null).ShouldBeTrue();
    }

    [Fact]
    public void SR_M_12_TenantScopedSubscription_NeverReceivesGlobalOrForeignEvents_EvenIfClusterWideSet()
    {
        var subscription = new WebhookSubscription { IsActive = true, IsClusterWide = true, TenantId = "tenant-a", EventFilters = ["*"] };

        subscription.MatchesEvent("x", "tenant-a").ShouldBeTrue();
        subscription.MatchesEvent("x", "tenant-b").ShouldBeFalse();
        subscription.MatchesEvent("x", null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, WebhookSubscription.MaxAllowedAttempts)]
    public void SR_M_13_MaxAttempts_IsClamped(int configured, int expected)
    {
        new WebhookSubscription { MaxAttempts = configured }.EffectiveMaxAttempts.ShouldBe(expected);
    }

    [Fact]
    public async Task DispatchEventAsync_DispatchesSignedCloudEventWithStandardHeaders()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var options = Options.Create(new GatewayOptions
        {
            Webhooks = new WebhooksOptions
            {
                Enabled = true,
                DefaultTimeoutSeconds = 5,
                Subscriptions =
                [
                    new WebhookSubscription
                    {
                        Id = "sub-1",
                        TargetUrl = "https://receiver.internal/webhook",
                        EventFilters = ["governance.*"],
                        Secret = "secret-key-456",
                        IsClusterWide = true,
                        MaxAttempts = 1
                    }
                ]
            }
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var dispatcher = new WebhookDispatcherService(httpClient, options, NullLogger<WebhookDispatcherService>.Instance, env);

        var @event = new AccessPlanCreatedEvent(
            EventId: "ev-test-100",
            PlanId: "plan-100",
            DatasetId: "sales.orders",
            RequesterSid: "admin-user",
            GrantCount: 2,
            Timestamp: DateTimeOffset.UtcNow);

        // Act
        await dispatcher.DispatchEventAsync(@event);

        // Assert
        handler.Requests.Count.ShouldBe(1);
        var req = handler.Requests[0];
        req.RequestUri.ShouldBe(new Uri("https://receiver.internal/webhook"));
        req.Headers.GetValues("ce-specversion").Single().ShouldBe("1.0");
        req.Headers.GetValues("ce-id").Single().ShouldBe("ev-test-100");
        req.Headers.GetValues("ce-type").Single().ShouldBe("governance.access_plan.created");
        req.Headers.GetValues("X-Autheris-Signature-256").Single().ShouldStartWith("sha256=");
        req.Headers.GetValues("X-Autheris-Timestamp").ShouldNotBeEmpty();
    }
}
