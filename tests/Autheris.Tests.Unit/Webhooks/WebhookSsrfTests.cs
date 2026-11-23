using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Webhooks;

[Trait("Category", "Security")]
public sealed class WebhookSsrfTests
{
    [Theory]
    [InlineData("https://127.0.0.1/hook", new[] { "127.0.0.1" })]
    [InlineData("https://10.0.0.5/hook", new[] { "10.0.0.5" })]
    [InlineData("https://169.254.169.254/latest/meta-data", new[] { "169.254.169.254" })]
    [InlineData("https://internal.corp/hook", new[] { "192.168.1.10" })]
    public async Task Private_loopback_metadata_blocked(string targetUrl, string[] resolvedIps)
    {
        var handler = new SsrfProtectionHandler(
            new ProdEnvironment(),
            Options.Create(new GatewayOptions()),
            EgressIntegrations.GovernanceNotifications,
            (_, _) => Task.FromResult(Array.ConvertAll(resolvedIps, IPAddress.Parse)));
        handler.InnerHandler = new OkInnerHandler();

        using var invoker = new HttpMessageInvoker(handler);
        await Should.ThrowAsync<SecurityException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, targetUrl), CancellationToken.None));
    }

    [Fact]
    public async Task Redirect_not_followed()
    {
        var audit = new InMemoryAuditWriter();
        var redirectHandler = new RedirectHandler();
        using var primary = SecureOutboundHttp.CreatePrimaryHandler(false, EgressAllowlist.Empty, false, (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        primary.AllowAutoRedirect.ShouldBeFalse();

        var clientFactory = new StubHttpClientFactory(() => new HttpClient(redirectHandler, disposeHandler: false));

        var dispatcher = new GovernanceNotificationDispatcher(
            Options.Create(new GovernanceNotificationOptions
            {
                Webhook = new GovernanceNotificationWebhookSettings { Url = "https://hooks.example.com/dg" }
            }),
            clientFactory,
            audit);

        var ok = await dispatcher.TryDispatchAsync(new GovernanceNotificationMessage(
            "SELF_GRANT", "tenant-a", "entra", "user:it", "Test redirect rejection.", DateTimeOffset.UtcNow));

        ok.ShouldBeFalse();
        redirectHandler.CallCount.ShouldBe(1);
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.WebhookDeliveryFailed && e.DetailsJson.Contains("http_redirect"));
    }

    [Fact]
    public async Task Http_scheme_rejected()
    {
        var audit = new InMemoryAuditWriter();
        var calls = 0;
        var clientFactory = new StubHttpClientFactory(() =>
        {
            calls++;
            return new HttpClient(new OkInnerHandler(), disposeHandler: false);
        });
        var dispatcher = new GovernanceNotificationDispatcher(
            Options.Create(new GovernanceNotificationOptions
            {
                Webhook = new GovernanceNotificationWebhookSettings { Url = "http://hooks.example.com/insecure" }
            }),
            clientFactory,
            audit);

        var ok = await dispatcher.TryDispatchAsync(new GovernanceNotificationMessage(
            "SELF_GRANT", "tenant-a", "entra", "user:it", "HTTP must fail.", DateTimeOffset.UtcNow));

        ok.ShouldBeFalse();
        calls.ShouldBe(0);
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.WebhookDeliveryFailed);
    }

    private sealed class ProdEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Autheris";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create();
    }

    private sealed class OkInnerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.redirect.example.com/steal");
            return Task.FromResult(response);
        }
    }
}
