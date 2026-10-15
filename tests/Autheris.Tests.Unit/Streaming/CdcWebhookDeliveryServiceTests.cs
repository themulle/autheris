namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CdcWebhookDeliveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private sealed class FakeOutbox : IWebhookOutbox
    {
        public ConcurrentQueue<WebhookOutboxMessage> Rows { get; } = new();

        public Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
        {
            if (Rows.Any(r => r.Source == message.Source && r.SubscriptionId == message.SubscriptionId && r.EventId == message.EventId))
            {
                return Task.FromResult(false);
            }

            Rows.Enqueue(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(string id, string leaseOwner, int statusCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RescheduleAsync(string id, string leaseOwner, DateTimeOffset nextAttemptAt, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeadLetterAsync(string id, string leaseOwner, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountPendingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Mimics the real enforcer's contract: tenant claim must match, a configurable deny set, and masks forbidden columns.</summary>
    private sealed class FakeEnforcer : IStreamRlsPolicyEnforcer
    {
        public bool Deny { get; set; }
        public HashSet<string> MaskedColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ClaimsPrincipal> Principals { get; } = [];
        public Func<Exception?>? Throw { get; set; }

        public ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(CdcEvent cdcEvent, ClaimsPrincipal subscriber, CancellationToken ct = default)
        {
            Principals.Add(subscriber);
            if (Throw?.Invoke() is { } ex)
            {
                throw ex;
            }

            var tenant = subscriber.FindFirst("tenant_id")?.Value;
            if (Deny || !string.Equals(tenant, cdcEvent.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(StreamSecurityDecision.Denied("secret-reason-text"));
            }

            var row = (cdcEvent.After ?? cdcEvent.Before)!
                .Where(kv => !MaskedColumns.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return ValueTask.FromResult(new StreamSecurityDecision(true, row));
        }
    }

    private sealed class FakeChannel : ICdcEventChannel
    {
        public Func<int, CancellationToken, IAsyncEnumerable<CdcEvent>>? OnSubscribe { get; set; }
        public int Subscriptions;

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default)
        {
            topic.ShouldBe("cdc_all");
            return OnSubscribe!(Interlocked.Increment(ref Subscriptions), ct);
        }
    }

    private sealed class Fixture
    {
        public FakeTimeProvider Time { get; } = new(Now);
        public FakeOutbox Outbox { get; } = new();
        public FakeEnforcer Enforcer { get; } = new();
        public FakeChannel Channel { get; } = new();
        public ICloudEventSubscriptionStore Store { get; set; } = new InMemoryCloudEventSubscriptionStore();
        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();
        public CdcWebhooksOptions Options { get; set; } = new() { Enabled = true, SubscriptionCacheSeconds = 5 };
        public GatewayOptions Gateway { get; set; } = new();
        public List<AuditLogEntry> AuditEntries { get; } = [];

        public Fixture()
        {
            Audit.RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(c =>
                {
                    AuditEntries.Add(c.Arg<AuditLogEntry>());
                    return Task.CompletedTask;
                });
        }

        public CdcWebhookDeliveryService CreateWith(Microsoft.Extensions.Logging.ILogger<CdcWebhookDeliveryService> logger) => Create(logger);

        public CdcWebhookDeliveryService Create(Microsoft.Extensions.Logging.ILogger<CdcWebhookDeliveryService>? logger = null)
        {
            var services = new ServiceCollection();
            services.AddScoped<IStreamRlsPolicyEnforcer>(_ => Enforcer);
            var monitor = Substitute.For<IOptionsMonitor<CdcWebhooksOptions>>();
            monitor.CurrentValue.Returns(_ => Options);
            return new CdcWebhookDeliveryService(
                Channel, Store, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new CloudEventTransformer(),
                Outbox, Audit, monitor, Microsoft.Extensions.Options.Options.Create(Gateway), Time, logger ?? NullLogger<CdcWebhookDeliveryService>.Instance);
        }

        public async Task<CloudEventWebhookSubscription> AddSubscription(
            string id = "sub-1", string tenant = "tenant-a", string filter = "*", string? sid = "S-1-5-21-CREATOR",
            DateTimeOffset? createdAt = null, bool snapshot = true)
        {
            var sub = new CloudEventWebhookSubscription(id, tenant, "https://example.test/h", filter, [], "enc:v1:x",
                CreatedBySid: snapshot ? sid : null,
                CreatedByGroupSids: snapshot ? ["G-1"] : null,
                CreatedByRoles: snapshot ? ["Analyst"] : null,
                CreatedAt: snapshot ? createdAt ?? Now : null);
            await Store.RegisterSubscriptionAsync(sub);
            return sub;
        }
    }

    private static CdcEvent Event(
        string tenant = "tenant-a", string eventId = "pg_orders_123", IReadOnlyDictionary<string, object?>? after = null) =>
        new(eventId, Orders, CdcOperation.Insert, tenant, null,
            after ?? new Dictionary<string, object?> { ["id"] = 7, ["name"] = "visible", ["ssn"] = "123-45-6789" }, Now);

    [Fact]
    public async Task Service_EnqueuesMaskedEnvelope_ForMatchingSubscription()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Enforcer.MaskedColumns.Add("ssn");
        var service = f.Create();

        await service.ProcessEventAsync(Event(), default);

        var row = f.Outbox.Rows.ShouldHaveSingleItem();
        row.Source.ShouldBe(WebhookSubscriptionSource.CloudEvent);
        row.SubscriptionId.ShouldBe("sub-1");
        row.OwnerTenantId.ShouldBe("tenant-a");
        row.EventTenantId.ShouldBe("tenant-a");
        row.PayloadJson.ShouldContain("visible");
        row.PayloadJson.ShouldNotContain("123-45-6789");
        row.EventType.ShouldBe("autheris.cdc.insert");
    }

    [Fact]
    public async Task Service_DropsEvent_WhenSubscriberLacksColumnPermission_NeverPersistsUnmaskedPayload()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Enforcer.MaskedColumns.Add("ssn");
        f.Enforcer.MaskedColumns.Add("name");
        var service = f.Create();

        await service.ProcessEventAsync(Event(), default);

        var row = f.Outbox.Rows.ShouldHaveSingleItem();
        foreach (var text in new[] { row.PayloadJson, row.EventId, row.EventSource, row.EventType, row.SubscriptionId })
        {
            text.ShouldNotContain("123-45-6789");
            text.ShouldNotContain("visible");
            text.ShouldNotContain("ssn");
        }
    }

    [Fact]
    public async Task Service_DropsEvent_WhenEnforcerDenies_AndNothingIsPersisted()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Enforcer.Deny = true;

        await f.Create().ProcessEventAsync(Event(), default);

        f.Outbox.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_DropsEvent_WhenCreatorSnapshotMissing()
    {
        var f = new Fixture();
        await f.AddSubscription(snapshot: false);

        await f.Create().ProcessEventAsync(Event(), default);

        f.Outbox.Rows.ShouldBeEmpty();
        f.Enforcer.Principals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_DropsEvent_WhenSnapshotExpired()
    {
        var f = new Fixture();
        await f.AddSubscription(createdAt: Now.AddDays(-91));

        await f.Create().ProcessEventAsync(Event(), default);

        f.Outbox.Rows.ShouldBeEmpty();
        f.Enforcer.Principals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_EvaluatesAsCreatorSnapshot_WithTenantFromSubscription()
    {
        var f = new Fixture();
        await f.AddSubscription(sid: "S-1-5-21-CREATOR");

        await f.Create().ProcessEventAsync(Event(), default);

        var principal = f.Enforcer.Principals.ShouldHaveSingleItem();
        principal.GetUserSid()!.Value.Value.ShouldBe("S-1-5-21-CREATOR");
        principal.FindFirst("tenant_id")!.Value.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task Service_UsesCurrentPolicy_RevokedCreator_StopsDelivery()
    {
        var f = new Fixture();
        await f.AddSubscription();
        var service = f.Create();
        await service.ProcessEventAsync(Event(eventId: "e1"), default);

        f.Enforcer.Deny = true; // creator's access revoked after the subscription was created
        await service.ProcessEventAsync(Event(eventId: "e2"), default);

        f.Outbox.Rows.Select(r => r.EventId).Count().ShouldBe(1);
    }

    [Fact]
    public async Task Service_DoesNotDeliver_CrossTenantEvent()
    {
        var f = new Fixture();
        await f.AddSubscription(tenant: "tenant-a");

        await f.Create().ProcessEventAsync(Event(tenant: "tenant-b"), default);

        f.Outbox.Rows.ShouldBeEmpty();
        f.Enforcer.Principals.ShouldBeEmpty(); // not even evaluated: the tenant-scoped lookup finds nothing
    }

    [Fact]
    public async Task Service_DoesNotDeliver_WhenStoreReturnsForeignTenantSubscription()
    {
        var f = new Fixture();
        var foreign = await f.AddSubscription(tenant: "tenant-a");
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.ListSubscriptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>([foreign]));
        f.Store = store;

        await f.Create().ProcessEventAsync(Event(tenant: "tenant-b"), default);

        f.Outbox.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_SubscriptionCache_IsKeyedByTenant()
    {
        var f = new Fixture();
        await f.AddSubscription(id: "a", tenant: "tenant-a");
        await f.AddSubscription(id: "b", tenant: "tenant-b", sid: "S-B");
        var service = f.Create();

        await service.ProcessEventAsync(Event(tenant: "tenant-a", eventId: "e1"), default);
        await service.ProcessEventAsync(Event(tenant: "tenant-b", eventId: "e2"), default);

        f.Outbox.Rows.Select(r => (r.SubscriptionId, r.EventTenantId)).ShouldBe([("a", "tenant-a"), ("b", "tenant-b")], ignoreOrder: true);
    }

    [Fact]
    public async Task Service_SubscriptionCache_ExpiresAfterConfiguredSeconds()
    {
        var f = new Fixture();
        var service = f.Create();
        await service.ProcessEventAsync(Event(eventId: "e0"), default);
        await f.AddSubscription();

        await service.ProcessEventAsync(Event(eventId: "e1"), default);
        f.Outbox.Rows.ShouldBeEmpty(); // still cached as "no subscriptions"

        f.Time.Advance(TimeSpan.FromSeconds(6));
        await service.ProcessEventAsync(Event(eventId: "e2"), default);
        f.Outbox.Rows.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Service_UntenantedEvent_IsNeverFannedOut(string? tenant)
    {
        var f = new Fixture();
        await f.AddSubscription();

        await f.Create().ProcessEventAsync(Event() with { TenantId = tenant }, default);

        f.Outbox.Rows.ShouldBeEmpty();
        f.Enforcer.Principals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_EnqueueTwiceSameEvent_StoresOnce()
    {
        var f = new Fixture();
        await f.AddSubscription();
        var service = f.Create();

        await service.ProcessEventAsync(Event(), default);
        await service.ProcessEventAsync(Event(), default);

        f.Outbox.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Service_EventIdIsOpaque_NoPrimaryKeyValues()
    {
        var f = new Fixture();
        await f.AddSubscription();

        await f.Create().ProcessEventAsync(Event(eventId: "dbo_T_5_secret-pk"), default);

        var row = f.Outbox.Rows.ShouldHaveSingleItem();
        row.EventId.ShouldStartWith("cdc-");
        row.EventId.Length.ShouldBeLessThanOrEqualTo(128);
        row.EventId.ShouldNotContain("secret-pk");
        row.PayloadJson.ShouldNotContain("secret-pk");
        row.PayloadJson.ShouldContain(row.EventId);
        CdcWebhookEventId.From(Event(eventId: "dbo_T_5_secret-pk")).ShouldBe(row.EventId); // deterministic
    }

    [Fact]
    public async Task Service_PayloadOverMax_IsDropped()
    {
        var f = new Fixture();
        f.Gateway = new GatewayOptions { Webhooks = new WebhooksOptions { MaxPayloadBytes = 1024 } };
        await f.AddSubscription();

        await f.Create().ProcessEventAsync(
            Event(after: new Dictionary<string, object?> { ["id"] = 1, ["blob"] = new string('x', 5000) }), default);

        f.Outbox.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_OverlongEventSource_IsDropped()
    {
        var f = new Fixture();
        await f.AddSubscription();
        var longTable = new TableIdentifier("d", "s", new string('t', 600));

        await f.Create().ProcessEventAsync(Event() with { Table = longTable }, default);

        f.Outbox.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_UnauthorizedEvents_DroppedSilently_NoReasonTextInLogs()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Enforcer.Deny = true;
        var logger = new CapturingLogger();
        var service = f.CreateWith(logger);

        await service.ProcessEventAsync(Event(), default);

        f.Outbox.Rows.ShouldBeEmpty();
        logger.Entries.Where(e => e.Level > Microsoft.Extensions.Logging.LogLevel.Debug).ShouldBeEmpty();
        logger.Entries.ShouldAllBe(e => !e.Message.Contains("secret-reason-text"));
    }

    [Fact]
    public async Task Service_EnforcerThrows_DropsEventWithoutCrashing_AndNextEventIsProcessed()
    {
        var f = new Fixture();
        await f.AddSubscription();
        var first = true;
        f.Enforcer.Throw = () =>
        {
            var ex = first ? new InvalidOperationException("boom") : null;
            first = false;
            return ex;
        };
        var service = f.Create();

        await service.ProcessEventAsync(Event(eventId: "e1"), default);
        await service.ProcessEventAsync(Event(eventId: "e2"), default);

        f.Outbox.Rows.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Service_WritesTierBBatchAuditWithoutPayload()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Enforcer.MaskedColumns.Add("ssn");
        var service = f.Create();
        await service.ProcessEventAsync(Event(eventId: "e1"), default);
        await service.ProcessEventAsync(Event(eventId: "e1"), default); // duplicate
        f.Enforcer.Deny = true;
        await service.ProcessEventAsync(Event(eventId: "e2"), default);
        f.AuditEntries.ShouldBeEmpty(); // aggregated, not per event

        await service.FlushAuditAsync(default);

        var entry = f.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.CdcWebhookDeliveryBatch);
        entry.TenantId.Value.ShouldBe("tenant-a");
        entry.ActorSid.Value.ShouldBe("S-1-5-21-CREATOR");
        entry.Decision.ShouldBe("ALLOW");
        using var details = JsonDocument.Parse(entry.DetailsJson);
        var root = details.RootElement;
        root.GetProperty("subscriptionId").GetString().ShouldBe("sub-1");
        root.GetProperty("enqueued").GetInt32().ShouldBe(1);
        root.GetProperty("deduped").GetInt32().ShouldBe(1);
        root.GetProperty("droppedDenied").GetInt32().ShouldBe(1);
        entry.DetailsJson.ShouldNotContain("visible");
        entry.DetailsJson.ShouldNotContain("123-45-6789");
        entry.DetailsJson.ShouldNotContain("orders");

        await service.FlushAuditAsync(default);
        f.AuditEntries.Count.ShouldBe(1); // counters were reset
    }

    [Fact]
    public async Task Service_ResubscribesAfterChannelFailure()
    {
        var f = new Fixture();
        await f.AddSubscription();
        var delivered = new TaskCompletionSource();
        f.Channel.OnSubscribe = (n, ct) => n == 1 ? Throwing(ct) : Single(Event(), ct);
        var service = f.Create();

        await service.StartAsync(default);
        for (var i = 0; i < 50 && f.Outbox.Rows.IsEmpty; i++)
        {
            await Task.Delay(20);
            f.Time.Advance(TimeSpan.FromSeconds(31));
        }

        await service.StopAsync(default);
        f.Channel.Subscriptions.ShouldBeGreaterThanOrEqualTo(2);
        f.Outbox.Rows.ShouldHaveSingleItem();
        delivered.TrySetResult();

        static async IAsyncEnumerable<CdcEvent> Throwing([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("channel failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        async IAsyncEnumerable<CdcEvent> Single(CdcEvent e, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return e;
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Service_FlushesAuditOnStop()
    {
        var f = new Fixture();
        await f.AddSubscription();
        f.Channel.OnSubscribe = (_, ct) => Hold(ct);
        var service = f.Create();
        await service.StartAsync(default);
        await service.ProcessEventAsync(Event(), default);

        await service.StopAsync(default);

        f.AuditEntries.ShouldHaveSingleItem();

        static async IAsyncEnumerable<CdcEvent> Hold([EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<CdcWebhookDeliveryService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
