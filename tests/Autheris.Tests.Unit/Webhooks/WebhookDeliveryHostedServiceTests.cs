namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebhookDeliveryHostedServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

    private readonly SqliteGovernanceRepository _repo;
    private readonly WebhookCapturingHandler _handler = new();
    private readonly WebhookManualTime _clock = new(Start);
    private readonly WebhookCapturingLogger<WebhookDeliveryHostedService> _logger = new();
    private readonly InMemoryCloudEventSubscriptionStore _ceStore = new();
    private WebhookDeliveryHostedService _service = null!;
    private IWebhookOutbox Outbox => _repo;

    public WebhookDeliveryHostedServiceTests()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=wh_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", SeedDemoData = false }
            }));
        Build(new WebhooksOptions());
    }

    public void Dispose() => _repo.Dispose();

    private void Build(WebhooksOptions webhooks, string environment = "Development")
    {
        var options = Options.Create(new GatewayOptions { Webhooks = webhooks });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var policy = new WebhookRetryPolicy(webhooks, _clock, () => 1.0);
        var client = new WebhookDeliveryClient(
            new HttpClient(_handler), options, policy, new WebhookCapturingLogger<WebhookDeliveryClient>(), env, _clock, (_, _, _) => Task.CompletedTask);
        var services = new ServiceCollection().AddSingleton<Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore>(_ceStore).BuildServiceProvider();
        _service = new WebhookDeliveryHostedService(
            Outbox, client, new WebhookSubscriptionResolver(options, services), policy, options, _logger, _clock);
    }

    private static WebhooksOptions Config(
        string? secret = "s3cret", int maxAttempts = 5, string subId = "sub-1", bool active = true,
        int batch = 8, int threshold = 20, int cooldown = 300) =>
        new()
        {
            DeliveryBatchSize = batch,
            AutoDisableAfterConsecutiveFailures = threshold,
            CircuitCooldownSeconds = cooldown,
            Subscriptions =
            [
                new WebhookSubscription { Id = subId, TargetUrl = "https://hooks.example.com/in", Secret = secret ?? string.Empty, IsClusterWide = true, MaxAttempts = maxAttempts, IsActive = active }
            ]
        };

    private WebhookOutboxMessage Msg(string eventId, string subId = "sub-1", int maxAttempts = 5, WebhookSubscriptionSource source = WebhookSubscriptionSource.Configured, string? owner = null) => new(
        Id: Guid.NewGuid().ToString("N"), Source: source, SubscriptionId: subId, OwnerTenantId: owner, EventTenantId: owner,
        EventId: eventId, EventType: "autheris.test", EventSource: "https://autheris.gateway/events", EventTime: _clock.GetUtcNow(),
        PayloadJson: "{}", Attempt: 0, MaxAttempts: maxAttempts, State: WebhookOutboxState.Pending, NextAttemptAt: _clock.GetUtcNow(),
        LeaseUntil: null, LeaseOwner: null, LastStatusCode: null, LastErrorCode: null, CreatedAt: _clock.GetUtcNow());

    private async Task<IReadOnlyList<WebhookDeadLetter>> DeadLettersAsync(string? tenant = null) =>
        await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(tenant, tenant is null), 50);

    [Fact]
    public async Task Success_CompletesRow_AndSendsOnce()
    {
        Build(Config());
        await Outbox.EnqueueAsync(Msg("e1"));

        (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(1);

        _handler.Calls.Count.ShouldBe(1);
        (await Outbox.CountPendingAsync()).ShouldBe(0);
        (await DeadLettersAsync()).ShouldBeEmpty();
        (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Http5xx_RescheduledWithGrowingDelay()
    {
        Build(Config());
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.RunOnceAsync("w1", CancellationToken.None); // attempt 1 -> retry in 5s (jitter fixed at 1.0)
        (await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow().AddSeconds(4), TimeSpan.FromMinutes(1), "probe")).ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await _service.RunOnceAsync("w1", CancellationToken.None); // attempt 2 -> retry in 10s
        (await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow().AddSeconds(9), TimeSpan.FromMinutes(1), "probe")).ShouldBeEmpty();
        var third = await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow().AddSeconds(10), TimeSpan.FromMinutes(1), "probe");

        var row = third.ShouldHaveSingleItem();
        row.Attempt.ShouldBe(3);
        row.LastStatusCode.ShouldBe(503);
        row.LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.HttpStatus);
        _handler.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Http4xx_DeadLetteredImmediately()
    {
        Build(Config());
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await Outbox.EnqueueAsync(Msg("e1", owner: null));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        var dead = (await DeadLettersAsync()).ShouldHaveSingleItem();
        dead.Attempt.ShouldBe(1);
        dead.LastStatusCode.ShouldBe(404);
        dead.LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.HttpStatus);
        _handler.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MaxAttempts_DeadLetters()
    {
        Build(Config(maxAttempts: 2));
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await Outbox.EnqueueAsync(Msg("e1", maxAttempts: 2));

        await _service.RunOnceAsync("w1", CancellationToken.None);
        (await DeadLettersAsync()).ShouldBeEmpty();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.RunOnceAsync("w1", CancellationToken.None);

        var dead = (await DeadLettersAsync()).ShouldHaveSingleItem();
        dead.Attempt.ShouldBe(2);
        dead.LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.MaxAttemptsExceeded);
        dead.LastStatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task ConsecutiveFailures_OpenCircuit_DefersSubscription()
    {
        Build(Config(batch: 1, threshold: 3, cooldown: 300));
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 6; i++)
        {
            await Outbox.EnqueueAsync(Msg($"e{i}"));
        }

        for (var i = 0; i < 3; i++)
        {
            (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(1);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Circuit open: nothing is claimable until the cooldown ends, although retry delays are only seconds.
        _clock.Advance(TimeSpan.FromSeconds(120));
        (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(0);
        _clock.Advance(TimeSpan.FromSeconds(300));
        (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(1);
        _handler.Calls.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Success_ResetsConsecutiveFailureCounter()
    {
        Build(Config(batch: 1, threshold: 2));
        var fail = true;
        _handler.Respond = _ => new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        for (var i = 0; i < 3; i++)
        {
            await Outbox.EnqueueAsync(Msg($"e{i}"));
        }

        await _service.RunOnceAsync("w1", CancellationToken.None); // failure 1
        fail = false;
        await _service.RunOnceAsync("w1", CancellationToken.None); // success resets
        fail = true;
        await _service.RunOnceAsync("w1", CancellationToken.None); // failure 1 again, circuit must not open

        _clock.Advance(TimeSpan.FromMinutes(1));
        (await _service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task UnknownSubscription_DeadLettersSubscriptionNotFound()
    {
        Build(Config(subId: "other"));
        await Outbox.EnqueueAsync(Msg("e1", subId: "gone"));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionNotFound);
        _handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task InactiveSubscription_DeadLettersSubscriptionInactive()
    {
        Build(Config(active: false));
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionInactive);
        _handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequireSecret_MissingAtDelivery_DeadLettersWithoutSending()
    {
        Build(Config(secret: null), environment: "Production");
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SecretMissing);
        _handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CloudEventSubscription_IsResolvedFromStore_ScopedToOwnerTenant()
    {
        Build(new WebhooksOptions());
        await _ceStore.RegisterSubscriptionAsync(new CloudEventWebhookSubscription(
            "ce-1", "tenant-A", "https://hooks.example.com/ce", "*", [CdcOperation.Insert], "ce-secret",
            CustomHeaders: new Dictionary<string, string> { ["X-Api-Key"] = "k" }));
        await Outbox.EnqueueAsync(Msg("e1", "ce-1", source: WebhookSubscriptionSource.CloudEvent, owner: "tenant-A"));
        await Outbox.EnqueueAsync(Msg("e2", "ce-1", source: WebhookSubscriptionSource.CloudEvent, owner: "tenant-B"));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        var (req, body) = _handler.Calls.Single();
        req.Headers.GetValues("X-Api-Key").Single().ShouldBe("k");
        WebhookSignature.TryVerify("ce-secret", req.Headers.GetValues("X-Autheris-Timestamp").Single(), body, req.Headers.GetValues("X-Autheris-Signature-256").Single(), _clock.GetUtcNow()).ShouldBeTrue();
        (await DeadLettersAsync("tenant-B")).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionNotFound);
    }

    [Fact]
    public async Task Shutdown_DuringDelivery_LeavesRowLeasedWithoutRecordingFailure()
    {
        Build(Config());
        using var cts = new CancellationTokenSource();
        _handler.Respond = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.RunOnceAsync("w1", cts.Token);

        (await DeadLettersAsync()).ShouldBeEmpty();
        var reclaimed = await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow().AddMinutes(5), TimeSpan.FromMinutes(1), "w2");
        reclaimed.ShouldHaveSingleItem().Attempt.ShouldBe(2);
    }

    [Fact]
    public async Task Housekeeping_PurgesOldDeliveredAndDeadRows()
    {
        Build(new WebhooksOptions { DeliveredRetentionHours = 24, DeadLetterRetentionDays = 14 });
        await Outbox.EnqueueAsync(Msg("old"));
        var claimed = await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow(), TimeSpan.FromMinutes(1), "w1");
        await Outbox.CompleteAsync(claimed.Single().Id, "w1", 200);

        _clock.Advance(TimeSpan.FromHours(25));
        (await _service.PurgeAsync(CancellationToken.None)).ShouldBe(1);
    }
}
