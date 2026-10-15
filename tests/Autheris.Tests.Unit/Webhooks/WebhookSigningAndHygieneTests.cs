namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Enqueue-side hygiene of the webhook facade and the durable row (WP-ARC-A-02).</summary>
[Trait("Category", "Security")]
public sealed class WebhookSigningAndHygieneTests : IDisposable
{
    private const string Secret = "TOP-SECRET-SIGNING-KEY";
    private const string UrlToken = "URLTOKEN-9f3a";
    private const string HeaderValue = "HEADER-VALUE-77c1";

    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=wh_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", SeedDemoData = false }
        }));

    public void Dispose() => _repo.Dispose();

    private WebhookDispatcherService Service(WebhooksOptions webhooks, string environment = "Development", WebhookCapturingLogger<WebhookDispatcherService>? logger = null)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new WebhookDispatcherService(_repo, Options.Create(new GatewayOptions { Webhooks = webhooks }), logger ?? new WebhookCapturingLogger<WebhookDispatcherService>(), env);
    }

    private static WebhooksOptions Config(string secret = Secret, bool? requireSecret = null, string? tenant = null, int maxAttempts = 3, int? maxPayloadBytes = null) => new()
    {
        RequireSecret = requireSecret,
        MaxPayloadBytes = maxPayloadBytes ?? 262144,
        Subscriptions =
        [
            new WebhookSubscription
            {
                Id = "s1",
                TargetUrl = $"https://receiver.internal/webhook?token={UrlToken}",
                Secret = secret,
                TenantId = tenant,
                IsClusterWide = tenant is null,
                MaxAttempts = maxAttempts
            }
        ]
    };

    private static AccessPlanCreatedEvent Event(string id = "ev-1", string? tenant = null) =>
        new(EventId: id, PlanId: "p", DatasetId: "d", RequesterSid: "u", GrantCount: 1, Timestamp: DateTimeOffset.UtcNow, TenantId: tenant);

    private string RawRows()
    {
        var sb = new StringBuilder();
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT * FROM WEBHOOK_OUTBOX";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++)
            {
                sb.Append(r.GetName(i)).Append('=').Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Enqueue_RowContainsNoSecretUrlOrCustomHeaders()
    {
        await Service(Config()).DispatchEventAsync(Event());
        var ce = new CloudEventWebhookSubscription(
            "ce-1", "tenant-A", $"https://ce.example.com/in?token={UrlToken}", "*", [CdcOperation.Insert], Secret,
            CustomHeaders: new Dictionary<string, string> { ["X-Api-Key"] = HeaderValue });
        var envelope = new CloudEventEnvelope("1.0", "ce-evt", "/autheris/cdc/t", "autheris.cdc.insert", DateTimeOffset.UtcNow, "application/json", "tenant-A", new { id = 1 });
        await ((IWebhookOutbox)_repo).EnqueueAsync(CloudEventOutboxMessageFactory.Create(ce, envelope, DateTimeOffset.UtcNow, 5));

        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(2);
        var raw = RawRows();
        raw.ShouldNotContain(Secret);
        raw.ShouldNotContain(UrlToken);
        raw.ShouldNotContain(HeaderValue);
        raw.ShouldNotContain("receiver.internal");
        raw.ShouldNotContain("ce.example.com");
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT group_concat(name) FROM pragma_table_info('WEBHOOK_OUTBOX')";
        var columns = (string)cmd.ExecuteScalar()!;
        columns.ShouldNotContain("url");
        columns.ShouldNotContain("secret");
        columns.ShouldNotContain("header");
    }

    [Fact]
    public async Task Dispatch_EnqueuesOneRowPerMatchingSubscription_WithOwnerAndAttemptBudget()
    {
        await Service(Config(tenant: "tenant-A", maxAttempts: 7)).DispatchEventAsync(Event(tenant: "tenant-A"));

        var rows = await ((IWebhookOutbox)_repo).ClaimBatchAsync(10, DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(1), "w");
        var row = rows.ShouldHaveSingleItem();
        row.OwnerTenantId.ShouldBe("tenant-A");
        row.EventTenantId.ShouldBe("tenant-A");
        row.MaxAttempts.ShouldBe(7);
        row.Source.ShouldBe(WebhookSubscriptionSource.Configured);
        row.PayloadJson.ShouldContain("\"id\":\"ev-1\"");
    }

    [Fact]
    public async Task Dispatch_SameEventTwice_IsDeduplicated()
    {
        var service = Service(Config());

        await service.DispatchEventAsync(Event("dup"));
        await service.DispatchEventAsync(Event("dup"));

        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RequireSecret_EmptySecret_IsNotEnqueued_AndLogsOnlyRedactedUrl()
    {
        var logger = new WebhookCapturingLogger<WebhookDispatcherService>();

        await Service(Config(secret: string.Empty, requireSecret: true), logger: logger).DispatchEventAsync(Event());

        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(0);
        logger.All.ShouldNotContain(UrlToken);
        logger.All.ShouldContain("https://receiver.internal/");
    }

    [Fact]
    public async Task EmptySecret_InDevelopment_IsEnqueuedByDefault_ButNotInProduction()
    {
        await Service(Config(secret: string.Empty), "Development").DispatchEventAsync(Event("dev"));
        await Service(Config(secret: string.Empty), "Production").DispatchEventAsync(Event("prod"));

        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task OversizedPayload_IsRejectedWithoutEnqueue()
    {
        var logger = new WebhookCapturingLogger<WebhookDispatcherService>();

        await Service(Config(maxPayloadBytes: 1024), logger: logger).DispatchEventAsync(
            new AccessPlanCreatedEvent(EventId: "big", PlanId: new string('p', 4000), DatasetId: "d", RequesterSid: "u", GrantCount: 1, Timestamp: DateTimeOffset.UtcNow));

        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(0);
        logger.All.ShouldContain(WebhookDeliveryErrorCodes.PayloadTooLarge);
    }

    [Fact]
    public async Task Dispatch_CompletesAfterEnqueue_WithoutAnyHttpCall()
    {
        // The facade has no HttpClient at all: delivery is the hosted service's job.
        typeof(WebhookDispatcherService).GetConstructors().Single().GetParameters()
            .ShouldNotContain(p => p.ParameterType == typeof(System.Net.Http.HttpClient));
        await Service(Config()).DispatchEventAsync(Event());
        (await ((IWebhookOutbox)_repo).CountPendingAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task OutboxFailure_IsLoggedWithoutMessage_AndDoesNotThrow()
    {
        var failing = Substitute.For<IWebhookOutbox>();
        failing.EnqueueAsync(Arg.Any<WebhookOutboxMessage>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("Host=db.internal;Password=hunter2"));
        var logger = new WebhookCapturingLogger<WebhookDispatcherService>();
        var service = new WebhookDispatcherService(failing, Options.Create(new GatewayOptions { Webhooks = Config() }), logger);

        await service.DispatchEventAsync(Event());

        logger.All.ShouldNotContain("hunter2");
        logger.All.ShouldContain(nameof(InvalidOperationException));
    }
}
