namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Products.Sync;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>
/// Full HTTP pipeline verification for <c>POST /api/v1/products/sync/webhook/{tenant}</c>, proving
/// <c>EdgeHeaderStrippingMiddleware</c> preserves inbound webhook signature headers while stripping internal headers.
/// </summary>
[Trait("Category", "Security")]
public sealed class ProductSyncWebhookIntegrationTests : IClassFixture<ProductSyncWebhookIntegrationTests.HostFixture>
{
    private const string Tenant = "acme";
    private const string WebhookSecretText = "webhook-integration-test-secret-32bytes";
    private readonly WebApplicationFactory<Program> _factory;

    public ProductSyncWebhookIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SignedWebhookWithAutherisHeaders_Returns202Accepted_AndReplayReturns409()
    {
        var client = _factory.CreateClient();
        var bodyText = """{"after":"0123456789abcdef0123456789abcdef"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        var secretBytes = Encoding.UTF8.GetBytes(WebhookSecretText);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = ProductSyncWebhookService.Sign(secretBytes, ts, bodyBytes);
        var deliveryId = "deliv-autheris-" + Guid.NewGuid().ToString("N");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/sync/webhook/{Tenant}")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Autheris-Timestamp", ts);
        req.Headers.Add("X-Autheris-Delivery", deliveryId);
        req.Headers.Add("X-Autheris-Signature-256", sig);

        var accepted = await client.SendAsync(req);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/sync/webhook/{Tenant}")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };
        replay.Headers.Add("X-Autheris-Timestamp", ts);
        replay.Headers.Add("X-Autheris-Delivery", deliveryId);
        replay.Headers.Add("X-Autheris-Signature-256", sig);

        var conflict = await client.SendAsync(replay);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SignedWebhookWithXWebhookHeaders_Returns202Accepted()
    {
        var client = _factory.CreateClient();
        var bodyText = """{"commit":"fedcba9876543210"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        var secretBytes = Encoding.UTF8.GetBytes(WebhookSecretText);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = ProductSyncWebhookService.Sign(secretBytes, ts, bodyBytes);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/sync/webhook/{Tenant}")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Webhook-Timestamp", ts);
        req.Headers.Add("X-Webhook-Delivery", "deliv-webhook-" + Guid.NewGuid().ToString("N"));
        req.Headers.Add("X-Webhook-Signature-256", sig);

        var response = await client.SendAsync(req);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task InvalidOrMissingOrMixedHeaders_Return401Unauthorized()
    {
        var client = _factory.CreateClient();
        var bodyText = """{"after":"0123456789abcdef"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        var secretBytes = Encoding.UTF8.GetBytes(WebhookSecretText);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = ProductSyncWebhookService.Sign(secretBytes, ts, bodyBytes);

        using var badSig = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/sync/webhook/{Tenant}")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };
        badSig.Headers.Add("X-Autheris-Timestamp", ts);
        badSig.Headers.Add("X-Autheris-Delivery", "deliv-bad-" + Guid.NewGuid().ToString("N"));
        badSig.Headers.Add("X-Autheris-Signature-256", "sha256=" + new string('0', 64));

        (await client.SendAsync(badSig)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var mixed = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/products/sync/webhook/{Tenant}")
        {
            Content = new StringContent(bodyText, Encoding.UTF8, "application/json")
        };
        mixed.Headers.Add("X-Autheris-Timestamp", ts);
        mixed.Headers.Add("X-Webhook-Delivery", "deliv-mixed-" + Guid.NewGuid().ToString("N"));
        mixed.Headers.Add("X-Webhook-Signature-256", sig);

        (await client.SendAsync(mixed)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "1000",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("ProductSyncWebhookIntegrationTests"),
                [ProductSyncWebhookService.SecretRef(new Autheris.Domain.Common.TenantId(Tenant))] = WebhookSecretText,
            });
        }
    }
}
