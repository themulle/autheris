namespace Autheris.Application.Webhooks;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// WP-ARC-A-02: drains the webhook outbox. Workers claim leased batches (disjoint across workers and nodes), deliver through
/// <see cref="WebhookDeliveryClient"/> and record the result. Retries use full-jitter backoff; permanent failures and exhausted
/// attempt budgets go to dead letters. A node-local failure counter pauses (circuit) a persistently failing subscription cluster-wide
/// by deferring its pending rows.
/// </summary>
public sealed class WebhookDeliveryHostedService : BackgroundService
{
    private static readonly Counter<long> DeliveriesCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_webhook_deliveries_total", description: "Webhook delivery attempts by outcome (success, retry, dead)");

    private static readonly Counter<long> DeadLettersCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_webhook_dead_letters_total", description: "Webhook deliveries moved to dead letters, by error code");

    private static long s_pending;

    // Process-wide (static) gauge fed by the polling workers; instances only update the value.
    private static readonly ObservableGauge<long> PendingGauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_webhook_outbox_pending", () => Interlocked.Read(ref s_pending), description: "Undelivered webhook outbox rows");

    private readonly IWebhookOutbox _outbox;
    private readonly WebhookDeliveryClient _client;
    private readonly WebhookSubscriptionResolver _resolver;
    private readonly WebhookRetryPolicy _retryPolicy;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<WebhookDeliveryHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(WebhookSubscriptionSource Source, string SubscriptionId), int> _consecutiveFailures = new();
    private long _lastPurgeTicks;

    public WebhookDeliveryHostedService(
        IWebhookOutbox outbox,
        WebhookDeliveryClient client,
        WebhookSubscriptionResolver resolver,
        WebhookRetryPolicy retryPolicy,
        IOptions<GatewayOptions> options,
        ILogger<WebhookDeliveryHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastPurgeTicks = _timeProvider.GetUtcNow().UtcTicks;
        _ = PendingGauge; // touch the static gauge so it is registered once the service exists
    }

    private WebhooksOptions Webhooks => _options.Value.Webhooks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Webhooks.EffectiveDeliveryWorkers;
        var node = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            var leaseOwner = $"{node}:{i}";
            var primary = i == 0;
            tasks[i] = Task.Run(() => RunWorkerAsync(leaseOwner, primary, stoppingToken), CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(string leaseOwner, bool primary, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Webhooks.EffectivePollIntervalSeconds), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    int claimed;
                    do
                    {
                        claimed = await RunOnceAsync(leaseOwner, ct).ConfigureAwait(false);
                    }
                    while (claimed >= Webhooks.EffectiveBatchSize && !ct.IsCancellationRequested);

                    if (primary)
                    {
                        await HousekeepingAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Messages of storage exceptions may carry connection details: log the type only.
                    _logger.LogError("Webhook delivery loop failed ({ErrorType}); retrying on the next tick.", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Claims and delivers one batch. Returns the number of claimed rows.</summary>
    internal async Task<int> RunOnceAsync(string leaseOwner, CancellationToken ct)
    {
        var webhooks = Webhooks;
        var now = _timeProvider.GetUtcNow();
        var batch = await _outbox.ClaimBatchAsync(webhooks.EffectiveBatchSize, now, webhooks.EffectiveLease, leaseOwner, ct).ConfigureAwait(false);
        foreach (var message in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break; // the remaining leased rows are reclaimed after the lease expires
            }

            await ProcessAsync(message, leaseOwner, ct).ConfigureAwait(false);
        }

        return batch.Count;
    }

    private async Task ProcessAsync(WebhookOutboxMessage message, string leaseOwner, CancellationToken ct)
    {
        var target = await _resolver.ResolveAsync(message, ct).ConfigureAwait(false);
        if (target is null)
        {
            await DeadAsync(message, leaseOwner, null, WebhookDeliveryErrorCodes.SubscriptionNotFound, ct).ConfigureAwait(false);
            return;
        }

        if (!target.IsActive)
        {
            await DeadAsync(message, leaseOwner, null, WebhookDeliveryErrorCodes.SubscriptionInactive, ct).ConfigureAwait(false);
            return;
        }

        var result = await _client.DeliverAsync(message, target, ct).ConfigureAwait(false);
        var decision = result.Decision;
        var key = (message.Source, message.SubscriptionId);

        switch (decision.Outcome)
        {
            case WebhookRetryOutcome.Success:
                _consecutiveFailures.TryRemove(key, out _);
                await _outbox.CompleteAsync(message.Id, leaseOwner, result.StatusCode ?? 200, ct).ConfigureAwait(false);
                DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                return;

            case WebhookRetryOutcome.Permanent:
                await DeadAsync(message, leaseOwner, result.StatusCode, decision.ErrorCode ?? WebhookDeliveryErrorCodes.Network, ct).ConfigureAwait(false);
                if (decision.ErrorCode != WebhookDeliveryErrorCodes.SecretMissing)
                {
                    await RecordFailureAsync(message, ct).ConfigureAwait(false);
                }

                return;

            default:
                if (decision.ErrorCode is null)
                {
                    return; // shutdown: neither retry nor failure recorded; the lease expires and the row is reclaimed
                }

                if (message.Attempt >= message.MaxAttempts)
                {
                    await DeadAsync(message, leaseOwner, result.StatusCode, WebhookDeliveryErrorCodes.MaxAttemptsExceeded, ct).ConfigureAwait(false);
                }
                else
                {
                    var next = _timeProvider.GetUtcNow() + _retryPolicy.ComputeDelay(message.Attempt, decision.RetryAfter);
                    await _outbox.RescheduleAsync(message.Id, leaseOwner, next, result.StatusCode, decision.ErrorCode, ct).ConfigureAwait(false);
                    DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
                }

                await RecordFailureAsync(message, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task DeadAsync(WebhookOutboxMessage message, string leaseOwner, int? statusCode, string errorCode, CancellationToken ct)
    {
        await _outbox.DeadLetterAsync(message.Id, leaseOwner, statusCode, errorCode, ct).ConfigureAwait(false);
        DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "dead"));
        DeadLettersCounter.Add(1, new KeyValuePair<string, object?>("code", errorCode));
        _logger.LogWarning(
            "Webhook {EventId} for subscription {SubscriptionId} dead-lettered ({ErrorCode}) after {Attempt} attempt(s).",
            message.EventId, message.SubscriptionId, errorCode, message.Attempt);
    }

    private async Task RecordFailureAsync(WebhookOutboxMessage message, CancellationToken ct)
    {
        var key = (message.Source, message.SubscriptionId);
        var failures = _consecutiveFailures.AddOrUpdate(key, 1, static (_, current) => current + 1);
        if (failures < Webhooks.EffectiveCircuitThreshold)
        {
            return;
        }

        // Circuit: pause the subscription's pending rows for everyone (state lives in the outbox, so it works across nodes).
        _consecutiveFailures.TryRemove(key, out _);
        var until = _timeProvider.GetUtcNow().AddSeconds(Webhooks.EffectiveCircuitCooldownSeconds);
        var deferred = await _outbox.DeferSubscriptionAsync(message.Source, message.SubscriptionId, until, ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Webhook subscription {SubscriptionId} failed {Failures} times in a row; {Deferred} pending delivery(ies) paused for {CooldownSeconds}s.",
            message.SubscriptionId, failures, deferred, Webhooks.EffectiveCircuitCooldownSeconds);
    }

    private async Task HousekeepingAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var last = new DateTimeOffset(Interlocked.Read(ref _lastPurgeTicks), TimeSpan.Zero);
        if (now - last >= TimeSpan.FromHours(1))
        {
            await PurgeAsync(ct).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref s_pending, await _outbox.CountPendingAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Removes expired delivered and dead rows (retention from <see cref="WebhooksOptions"/>).</summary>
    internal async Task<int> PurgeAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        Interlocked.Exchange(ref _lastPurgeTicks, now.UtcTicks);
        var webhooks = Webhooks;
        return await _outbox.PurgeAsync(
            now.AddHours(-Math.Max(1, webhooks.DeliveredRetentionHours)),
            now.AddDays(-Math.Max(1, webhooks.DeadLetterRetentionDays)),
            ct).ConfigureAwait(false);
    }
}
