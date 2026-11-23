using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Notifications;

[Trait("Category", "Security")]
public sealed class GovernanceNotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    [Fact]
    public async Task Unset_url_noop()
    {
        var calls = 0;
        var httpFactory = new StubHttpClientFactory(() =>
        {
            calls++;
            return new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), disposeHandler: false);
        });
        var audit = new InMemoryAuditWriter();
        var dispatcher = new GovernanceNotificationDispatcher(
            Options.Create(new GovernanceNotificationOptions { Webhook = new GovernanceNotificationWebhookSettings { Url = "" } }),
            httpFactory,
            audit);

        var ok = await dispatcher.TryDispatchAsync(new GovernanceNotificationMessage(
            "SELF_GRANT", "tenant-a", "entra", "user:it", "IT admin self-grant.", Now));
        ok.ShouldBeTrue();
        calls.ShouldBe(0);
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Url_not_in_audit_or_logs()
    {
        const string secretUrl = "https://hooks.slack.com/services/T000/B000/SUPER_SECRET_TOKEN_XYZ";
        var logs = new List<string>();
        var logger = new CapturingLogger<GovernanceNotificationDispatcher>(logs);
        var audit = new InMemoryAuditWriter();

        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var clientFactory = new StubHttpClientFactory(() => new HttpClient(handler, disposeHandler: false));

        var dispatcher = new GovernanceNotificationDispatcher(
            Options.Create(new GovernanceNotificationOptions { Webhook = new GovernanceNotificationWebhookSettings { Url = secretUrl } }),
            clientFactory,
            audit,
            logger: logger,
            timeProvider: new FakeTimeProvider(Now));

        await dispatcher.AuditStartupChannelStateAsync();
        await dispatcher.DispatchAsync(new GovernanceNotificationMessage(
            "SELF_GRANT", "tenant-a", "entra", "user:it", "Self-grant executed.", Now));

        string.Join("\n", logs).ShouldNotContain("SUPER_SECRET_TOKEN_XYZ");
        string.Join("\n", logs).ShouldNotContain("hooks.slack.com");
        foreach (var entry in audit.Entries)
        {
            entry.DetailsJson.ShouldNotContain("SUPER_SECRET_TOKEN_XYZ");
            entry.DetailsJson.ShouldNotContain("hooks.slack.com");
        }
    }

    [Fact]
    public async Task Channel_state_change_audited()
    {
        const string webhookUrl = "https://hooks.teams.example.com/webhook/secret-token-123";
        var audit = new InMemoryAuditWriter();
        var repo = new InMemoryPolicyRepo();

        var protector = new EchoTestProtector();
        var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(new AccessPolicyOptions()),
            audit,
            new FakeTimeProvider(Now),
            protector);

        var res = await provider.UpdateAsync(
            new TenantId("tenant-a"),
            new TenantAccessPolicyUpdate(NotificationWebhookUrl: webhookUrl),
            0,
            new Sid("user:dg"));

        res.Status.ShouldBe(AccessPolicyUpdateStatus.Updated);
        res.Policy!.NotificationWebhookConfigured.ShouldBeTrue();
        res.Policy.NotificationWebhookHost.ShouldBe("hooks.teams.example.com");

        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.NotificationChannelChanged && e.Decision == "CONFIGURED");
        foreach (var entry in audit.Entries)
        {
            entry.DetailsJson.ShouldNotContain("secret-token-123");
            entry.DetailsJson.ShouldNotContain("hooks.teams.example.com");
        }
    }

    private sealed class StubHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create();
    }

    private sealed class InMemoryPolicyRepo : IAccessPolicySettingsRepository
    {
        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<StoredTenantAccessPolicy?>(null);

        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            Task.FromResult(new AccessPolicyWriteResult(
                AccessPolicyWriteStatus.Created,
                new StoredTenantAccessPolicy(tenant, settings, 1, actor.Value, Now)));

        public Task<bool> DeleteIfVersionAsync(TenantId tenant, int expectedVersion, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class EchoTestProtector : INotificationWebhookUrlProtector
    {
        public string Protect(string plaintextUrl) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintextUrl));
        public string Unprotect(string protectedUrl) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedUrl["enc:".Length..]));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class CapturingLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
