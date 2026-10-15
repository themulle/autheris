namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// WP-ARC-A-03: turns the CDC event stream into durable webhook outbox rows. For every event and every matching tenant subscription the
/// event is authorised as the subscription creator's principal snapshot against the CURRENT policies (<see cref="IStreamRlsPolicyEnforcer"/>:
/// tenant, consent, Casbin, ReBAC, row filter, column masking) and only the masked result is enqueued. The raw event never reaches the
/// outbox or the logs. Network delivery is not done here (see <see cref="WebhookDeliveryHostedService"/>); the channel is at-most-once, so
/// this service only enqueues. Authorisation failures drop the event silently (no reason text above Debug level).
/// </summary>
public sealed class CdcWebhookDeliveryService : BackgroundService
{
    private const string Topic = "cdc_all";

    // CDC subscriptions have no per-subscription attempt budget; the documented default of a configured webhook applies.
    private static readonly int DefaultMaxAttempts = new WebhookSubscription().EffectiveMaxAttempts;

    private static readonly Counter<long> DroppedCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_webhook_dropped_total", description: "CDC webhook candidates dropped before enqueue, by reason");

    private static readonly Counter<long> EnqueuedCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_webhook_enqueued_total", description: "CDC webhook outbox rows enqueued");

    private readonly ICdcEventChannel _channel;
    private readonly ICloudEventSubscriptionStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICloudEventTransformer _transformer;
    private readonly IWebhookOutbox _outbox;
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<CdcWebhooksOptions> _options;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<CdcWebhookDeliveryService> _logger;
    private readonly CdcStreamCompatFeed? _compatFeed;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Subscription), AuditBatch> _batches = [];
    private readonly object _batchGate = new();

    public CdcWebhookDeliveryService(
        ICdcEventChannel channel,
        ICloudEventSubscriptionStore store,
        IServiceScopeFactory scopeFactory,
        ICloudEventTransformer transformer,
        IWebhookOutbox outbox,
        IAuditWriter audit,
        IOptionsMonitor<CdcWebhooksOptions> options,
        IOptions<GatewayOptions> gatewayOptions,
        TimeProvider time,
        ILogger<CdcWebhookDeliveryService> logger,
        CdcStreamCompatFeed? compatFeed = null)
    {
        _compatFeed = compatFeed;
        _channel = channel;
        _store = store;
        _scopeFactory = scopeFactory;
        _transformer = transformer;
        _outbox = outbox;
        _audit = audit;
        _options = options;
        _gatewayOptions = gatewayOptions;
        _time = time;
        _logger = logger;
    }

    private const int MaxCachedTenants = 1024;

    /// <summary>Number of tenants currently cached (bounded by <see cref="MaxCachedTenants"/>).</summary>
    internal int CachedTenantCount => _cache.Count;

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, IReadOnlyList<CloudEventWebhookSubscription> Subscriptions);

    private sealed class AuditBatch
    {
        public string? ActorSid;
        public int Enqueued;
        public int Deduped;
        public int DroppedDenied;
        public int DroppedBounds;
        public DateTimeOffset From;
        public DateTimeOffset To;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flusher = FlushLoopAsync(stoppingToken);
        // I1 compatibility feed (D-A13): MSSQL-tracked tables come from the durable stream, everything else from the channel.
        var compat = _compatFeed is null ? Task.CompletedTask : _compatFeed.RunAsync(ProcessStreamEventAsync, stoppingToken);
        try
        {
            await ConsumeLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            await flusher.ConfigureAwait(false);
            await compat.ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushAuditAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var cdcEvent in _channel.SubscribeAsync(Topic, ct).ConfigureAwait(false))
                {
                    failures = 0;
                    await ProcessEventAsync(cdcEvent, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Type only: messages of channel/storage exceptions can carry sensitive details.
                _logger.LogError("CDC webhook subscription to the event channel failed ({ErrorType}); resubscribing.", ex.GetType().Name);
            }

            failures++;
            var seconds = Math.Min(30d, Math.Pow(2, Math.Min(failures, 5)));
            var delay = TimeSpan.FromSeconds(Math.Max(1d, seconds * (0.5 + (Random.Shared.NextDouble() * 0.5))));
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.AuditBatchSeconds)), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await FlushAuditAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("CDC webhook audit batch flush failed ({ErrorType}).", ex.GetType().Name);
                }

                // Picks up a changed interval (options monitor) for the next tick.
                timer.Period = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.AuditBatchSeconds));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: StopAsync performs the final flush.
        }
    }

    /// <summary>Processes one event of the durable stream; its opaque stream event id is used as the CloudEvent id (D-A10).</summary>
    internal Task ProcessStreamEventAsync(CdcEvent cdcEvent, CancellationToken ct) => ProcessEventAsync(cdcEvent, fromStream: true, ct);

    /// <summary>Processes one event: never throws for per-event failures (the stream must keep flowing).</summary>
    internal Task ProcessEventAsync(CdcEvent cdcEvent, CancellationToken ct) => ProcessEventAsync(cdcEvent, fromStream: false, ct);

    private async Task ProcessEventAsync(CdcEvent cdcEvent, bool fromStream, CancellationToken ct)
    {
        try
        {
            await ProcessCoreAsync(cdcEvent, fromStream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Drop("error");
            _logger.LogError("CDC webhook event processing failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    private async Task ProcessCoreAsync(CdcEvent cdcEvent, bool fromStream, CancellationToken ct)
    {
        // Never fan out an untenanted event.
        if (string.IsNullOrWhiteSpace(cdcEvent.TenantId))
        {
            Drop("no_tenant");
            return;
        }

        var options = _options.CurrentValue;
        var webhooks = _gatewayOptions.Value.Webhooks;
        var subscriptions = await GetSubscriptionsAsync(cdcEvent, options, ct).ConfigureAwait(false);
        foreach (var subscription in subscriptions)
        {
            // Defence in depth: the store lookup is tenant-scoped, but a foreign row must never be served.
            if (!string.Equals(subscription.TenantId, cdcEvent.TenantId, StringComparison.Ordinal))
            {
                Drop("tenant_mismatch");
                continue;
            }

            // History replay (initial sync, snapshot re-reads): a subscription only receives changes that happened after its creation.
            if (subscription.CreatedAt is { } createdAt && cdcEvent.Timestamp < createdAt)
            {
                Drop("before_subscription");
                continue;
            }

            var denial = CdcSubscriptionPrincipal.Evaluate(subscription, _time, options, out var principal);
            if (principal is null)
            {
                Drop(denial == CdcSnapshotDenial.Expired ? "snapshot_expired" : "no_snapshot");
                Record(subscription, b => b.DroppedDenied++);
                continue;
            }

            StreamSecurityDecision decision;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var enforcer = scope.ServiceProvider.GetRequiredService<IStreamRlsPolicyEnforcer>();
                decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, principal, ct).ConfigureAwait(false);
            }

            if (!decision.IsAllowed || decision.MaskedPayload is not { Count: > 0 })
            {
                Drop("denied");
                Record(subscription, b => b.DroppedDenied++);
                continue;
            }

            var envelope = _transformer.Transform(cdcEvent, decision.MaskedPayload) with { Id = fromStream ? cdcEvent.EventId : CdcWebhookEventId.From(cdcEvent) };

            WebhookOutboxMessage message;
            try
            {
                message = CloudEventOutboxMessageFactory.Create(
                    subscription, envelope, _time.GetUtcNow(), DefaultMaxAttempts, webhooks.EffectiveMaxPayloadBytes);
            }
            catch (WebhookOutboxBoundsException)
            {
                Drop("bounds");
                Record(subscription, b => b.DroppedBounds++);
                continue;
            }

            bool inserted;
            try
            {
                inserted = await _outbox.EnqueueAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // At-most-once at channel level: the event is lost for this subscription. Type only.
                Drop("enqueue_failed");
                _logger.LogError("CDC webhook enqueue failed ({ErrorType}).", ex.GetType().Name);
                continue;
            }

            if (inserted)
            {
                EnqueuedCounter.Add(1);
            }

            Record(subscription, b =>
            {
                if (inserted)
                {
                    b.Enqueued++;
                }
                else
                {
                    b.Deduped++;
                }
            });
        }
    }

    private async ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(
        CdcEvent cdcEvent, CdcWebhooksOptions options, CancellationToken ct)
    {
        var tenant = cdcEvent.TenantId!;
        var now = _time.GetUtcNow();
        if (!_cache.TryGetValue(tenant, out var entry) || entry.ExpiresAt <= now)
        {
            // All subscriptions of the tenant are cached (not per table), then matched in memory.
            var all = await _store.ListSubscriptionsAsync(tenant, ct).ConfigureAwait(false);
            entry = new CacheEntry(now.AddSeconds(options.SubscriptionCacheSeconds), all);
            if (_cache.Count >= MaxCachedTenants)
            {
                foreach (var (key, value) in _cache)
                {
                    if (value.ExpiresAt <= now)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }

                if (_cache.Count >= MaxCachedTenants)
                {
                    _cache.Clear(); // hard cap: every tenant is just re-read on its next event
                }
            }

            _cache[tenant] = entry;
        }

        return entry.Subscriptions
            .Where(s => CloudEventSubscriptionMatcher.Matches(s, cdcEvent.Table, cdcEvent.Operation))
            .ToList();
    }

    private void Drop(string reason)
    {
        DroppedCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
        // Reason code only: never the enforcer's reason text, never event content.
        _logger.LogDebug("CDC webhook candidate dropped ({Reason}).", reason);
    }

    private void Record(CloudEventWebhookSubscription subscription, Action<AuditBatch> update)
    {
        var now = _time.GetUtcNow();
        lock (_batchGate)
        {
            if (!_batches.TryGetValue((subscription.TenantId, subscription.Id), out var batch))
            {
                batch = new AuditBatch { From = now, ActorSid = subscription.CreatedBySid };
                _batches[(subscription.TenantId, subscription.Id)] = batch;
            }

            batch.To = now;
            update(batch);
        }
    }

    /// <summary>Writes one Tier-B audit event per (tenant, subscription) with activity since the last flush. Details never contain event content.</summary>
    internal async Task FlushAuditAsync(CancellationToken ct)
    {
        List<((string Tenant, string Subscription) Key, AuditBatch Batch)> pending;
        lock (_batchGate)
        {
            pending = _batches.Select(kv => (kv.Key, kv.Value)).ToList();
            _batches.Clear();
        }

        foreach (var (key, batch) in pending)
        {
            var details = JsonSerializer.Serialize(new
            {
                subscriptionId = key.Subscription,
                enqueued = batch.Enqueued,
                deduped = batch.Deduped,
                droppedDenied = batch.DroppedDenied,
                droppedBounds = batch.DroppedBounds,
                from = batch.From,
                to = batch.To
            });

            await _audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = new TenantId(key.Tenant),
                EventType = AuditEventTypes.CdcWebhookDeliveryBatch,
                ActorSid = new Sid(batch.ActorSid ?? string.Empty),
                Decision = batch.Enqueued == 0 && batch.Deduped == 0 && (batch.DroppedDenied > 0 || batch.DroppedBounds > 0) ? "DENY" : "ALLOW",
                DetailsJson = details,
                OccurredAt = _time.GetUtcNow()
            }, ct).ConfigureAwait(false);
        }
    }
}
