using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Webhooks;

[Trait("Category", "Security")]
public sealed class WebhookFinalFailureAuditTests
{
    [Fact]
    public async Task Final_failure_audited()
    {
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        var audit = new InMemoryAuditWriter();
        var clientFactory = new StubHttpClientFactory(() => new HttpClient(new FailingHandler(), disposeHandler: false));

        var dispatcher = new GovernanceNotificationDispatcher(
            Options.Create(new GovernanceNotificationOptions
            {
                Webhook = new GovernanceNotificationWebhookSettings { Url = "https://hooks.example.com/dg/secret-path" }
            }),
            clientFactory,
            audit,
            timeProvider: new FakeTimeProvider(now));

        var ok = await dispatcher.TryDispatchAsync(new GovernanceNotificationMessage(
            "AUDIT_CHAIN_BROKEN", "tenant-a", "entra", "system", "Chain integrity broken.", now));

        ok.ShouldBeFalse();
        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.WebhookDeliveryFailed);
        entry.Decision.ShouldBe("FAILED");
        entry.DetailsJson.ShouldNotContain("secret-path");
    }

    private sealed class StubHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create();
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
