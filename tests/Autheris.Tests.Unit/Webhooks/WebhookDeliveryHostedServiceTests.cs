using Autheris.Tests.Common;
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
    private readonly WebhookCapturingLogger<WebhookDeliveryClient> _clientLogger = new();
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

    private void Build(WebhooksOptions webhooks, string environment = "Development", Func<Uri, bool, CancellationToken, Task>? validator = null)
    {
        var options = Options.Create(new GatewayOptions { Webhooks = webhooks });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var policy = new WebhookRetryPolicy(webhooks, _clock, () => 1.0);
        var client = new WebhookDeliveryClient(
            new HttpClient(_handler), options, policy, _clientLogger, env, _clock, validator ?? ((_, _, _) => Task.CompletedTask));
        var services = new ServiceCollection().AddSingleton<Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore>(_ceStore).BuildServiceProvider();
        _service = new WebhookDeliveryHostedService(
            Outbox, client, new WebhookSubscriptionResolver(options, services), policy, options, _logger, _clock);
    }

    private static WebhooksOptions Config(
        string? secret = "s3cret", int maxAttempts = 5, string subId = "sub-1", bool active = true,
        int batch = 8, int threshold = 20, int cooldown = 300, string? tenant = null) =>
        new()
        {
            DeliveryBatchSize = batch,
            AutoDisableAfterConsecutiveFailures = threshold,
            CircuitCooldownSeconds = cooldown,
            Subscriptions =
            [
                new WebhookSubscription { Id = subId, TargetUrl = "https://hooks.example.com/in", Secret = secret ?? string.Empty, IsClusterWide = tenant is null, TenantId = tenant, MaxAttempts = maxAttempts, IsActive = active }
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
        await Outbox.CompleteAsync(claimed.Single().Id, "w1", 200, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromHours(25));
        (await _service.PurgeAsync(CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public async Task Resolver_SubscriptionRebound_ToAnotherTenant_DeadLettersWithoutSending()
    {
        Build(Config(tenant: "tenant-A"));
        await Outbox.EnqueueAsync(Msg("e1", owner: "tenant-A"));

        Build(Config(tenant: "tenant-B")); // admin re-binds the subscription before the retry
        await _service.RunOnceAsync("w1", CancellationToken.None);

        _handler.Calls.ShouldBeEmpty();
        (await DeadLettersAsync("tenant-A")).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionNotFound);
    }

    [Fact]
    public async Task Resolver_SubscriptionNoLongerClusterWide_DeadLettersWithoutSending()
    {
        Build(Config());
        await Outbox.EnqueueAsync(Msg("e1", owner: null));

        Build(new WebhooksOptions
        {
            Subscriptions = [new WebhookSubscription { Id = "sub-1", TargetUrl = "https://hooks.example.com/in", Secret = "s", IsClusterWide = false }]
        });
        await _service.RunOnceAsync("w1", CancellationToken.None);

        _handler.Calls.ShouldBeEmpty();
        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionNotFound);
    }

    [Fact]
    public async Task Resolver_EventFilterNoLongerMatches_DeadLettersWithoutSending()
    {
        Build(Config());
        await Outbox.EnqueueAsync(Msg("e1"));

        Build(new WebhooksOptions
        {
            Subscriptions = [new WebhookSubscription { Id = "sub-1", TargetUrl = "https://hooks.example.com/in", Secret = "s", IsClusterWide = true, EventFilters = ["other.*"] }]
        });
        await _service.RunOnceAsync("w1", CancellationToken.None);

        _handler.Calls.ShouldBeEmpty();
        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.SubscriptionNotFound);
    }

    [Fact]
    public async Task ExhaustedAttemptBudget_AfterRepeatedLeaseExpiry_DeadLettersWithoutSending()
    {
        Build(Config(maxAttempts: 1));
        await Outbox.EnqueueAsync(Msg("e1", maxAttempts: 1));
        // Poison row: a crashed worker claimed it (attempt 1) and never finished.
        await Outbox.ClaimBatchAsync(5, _clock.GetUtcNow(), TimeSpan.FromMinutes(1), "crashed");
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _service.RunOnceAsync("w1", CancellationToken.None); // attempt 2 > MaxAttempts 1

        _handler.Calls.ShouldBeEmpty();
        (await DeadLettersAsync()).ShouldHaveSingleItem().LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.MaxAttemptsExceeded);
    }

    [Fact]
    public async Task FailingRow_DoesNotAbortTheRestOfTheBatch()
    {
        // A CloudEvent row whose store throws, followed by a healthy configured row in the same batch.
        var throwing = Substitute.For<Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore>();
        throwing.GetSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<CloudEventWebhookSubscription?>>(_ => throw new InvalidOperationException("Password=hunter2"));
        var options = Options.Create(new GatewayOptions { Webhooks = Config() });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var policy = new WebhookRetryPolicy(options.Value.Webhooks, _clock, () => 1.0);
        var client = new WebhookDeliveryClient(new HttpClient(_handler), options, policy, new WebhookCapturingLogger<WebhookDeliveryClient>(), env, _clock, (_, _, _) => Task.CompletedTask);
        var services = new ServiceCollection().AddSingleton(throwing).BuildServiceProvider();
        var service = new WebhookDeliveryHostedService(Outbox, client, new WebhookSubscriptionResolver(options, services), policy, options, _logger, _clock);
        await Outbox.EnqueueAsync(Msg("bad", "ce-1", source: WebhookSubscriptionSource.CloudEvent, owner: "tenant-A"));
        await Outbox.EnqueueAsync(Msg("good"));

        (await service.RunOnceAsync("w1", CancellationToken.None)).ShouldBe(2);

        _handler.Calls.Count.ShouldBe(1);
        _logger.All.ShouldNotContain("hunter2");
        _logger.All.ShouldContain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Delivery_ErrorMessage_ContainsNoResolvedIp()
    {
        Build(Config(), validator: (_, _, _) =>
        {
            var ex = new System.Security.SecurityException("blocked 10.20.30.40 internal detail");
            ex.Data["ResolvedIp"] = "10.20.30.40";
            throw ex;
        });
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.RunOnceAsync("w1", CancellationToken.None);

        var dead = (await DeadLettersAsync()).ShouldHaveSingleItem();
        dead.LastErrorCode.ShouldBe(WebhookDeliveryErrorCodes.EgressDenied);
        var raw = string.Join("|", dead.LastErrorCode, dead.LastStatusCode, dead.EventId, dead.SubscriptionId);
        raw.ShouldNotContain("10.20.30.40");
        foreach (var log in new[] { _logger.All, _clientLogger.All })
        {
            log.ShouldNotContain("10.20.30.40");
            log.ShouldNotContain("internal detail");
        }
    }

    [Fact]
    public async Task Housekeeping_CountsPendingAtMostEvery30Seconds()
    {
        var outbox = Substitute.For<IWebhookOutbox>();
        outbox.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(0L);
        var options = Options.Create(new GatewayOptions { Webhooks = new WebhooksOptions() });
        var policy = new WebhookRetryPolicy(options.Value.Webhooks, _clock);
        var client = new WebhookDeliveryClient(new HttpClient(_handler), options, policy, _clientLogger, null, _clock);
        var service = new WebhookDeliveryHostedService(outbox, client, new WebhookSubscriptionResolver(options, new ServiceCollection().BuildServiceProvider()), policy, options, _logger, _clock);

        await service.HousekeepingAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await service.HousekeepingAsync(CancellationToken.None);
        await outbox.Received(1).CountPendingAsync(Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromSeconds(25));
        await service.HousekeepingAsync(CancellationToken.None);
        await outbox.Received(2).CountPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StartsWorkers_DeliversOnTimerTicks_AndStopsCleanly()
    {
        Build(Config());
        await Outbox.EnqueueAsync(Msg("e1"));

        await _service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _clock.TimerCount == new WebhooksOptions().EffectiveDeliveryWorkers);
        _handler.Calls.ShouldBeEmpty(); // nothing happens before the first tick

        _clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => _handler.Calls.Count == 1);

        await _service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        _clock.TimerCount.ShouldBe(0);
        (await Outbox.CountPendingAsync()).ShouldBe(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await WaitHelper.WaitUntil(condition, TimeSpan.FromSeconds(10), "Condition not reached.");
    }
}
