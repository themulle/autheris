namespace Autheris.Application.ConsentRequests.Events;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Drains leased consent event deliveries from <see cref="IConsentEventOutbox"/>, signs CloudEvents 1.0 requests
/// with each external workflow system's protected secret via <see cref="WebhookDeliveryClient"/>, retries transient
/// failures with <see cref="WebhookRetryPolicy"/>, and dead-letters exhausted or permanently failing deliveries.
/// </summary>
public sealed class ConsentWebhookDispatcherHostedService : BackgroundService
{
    private static readonly Counter<long> DeliveriesCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_consent_webhook_deliveries_total",
        description: "Consent webhook delivery attempts by outcome (success, retry, dead)");

    private static readonly Counter<long> DeadLettersCounter = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_consent_webhook_dead_letters_total",
        description: "Consent webhook deliveries moved to dead letters, by error code");

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);

    private readonly IConsentEventOutbox _outbox;
    private readonly IConsentWorkflowConfigRepository _workflowConfig;
    private readonly WebhookDeliveryClient _client;
    private readonly WebhookRetryPolicy _retryPolicy;
    private readonly IOptions<ConsentEventsOptions> _options;
    private readonly ILogger<ConsentWebhookDispatcherHostedService> _logger;
    private readonly IWebhookSecretProtector? _secretProtector;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;
    private long _lastPurgeTicks;

    public ConsentWebhookDispatcherHostedService(
        IConsentEventOutbox outbox,
        IConsentWorkflowConfigRepository workflowConfig,
        WebhookDeliveryClient client,
        WebhookRetryPolicy retryPolicy,
        IOptions<ConsentEventsOptions> options,
        ILogger<ConsentWebhookDispatcherHostedService> logger,
        IWebhookSecretProtector? secretProtector = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _auditWriter = auditWriter;
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _workflowConfig = workflowConfig ?? throw new ArgumentNullException(nameof(workflowConfig));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _secretProtector = secretProtector;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastPurgeTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Clamp(_options.Value.PollIntervalSeconds, 1, 3600);
        var leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds), _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(leaseOwner, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Consent webhook dispatcher loop failed ({ErrorType}); retrying on next tick.",
                        ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    internal async Task<int> RunOnceAsync(string leaseOwner, CancellationToken ct)
    {
        var opts = _options.Value;
        var maxAttempts = Math.Clamp(opts.MaxAttempts, 1, 100);
        var retentionDays = Math.Clamp(opts.RetentionDays, 1, 3650);
        var batchSize = Math.Clamp(opts.BatchSize, 1, 500);
        var leaseDuration = TimeSpan.FromSeconds(Math.Clamp(opts.LeaseSeconds, 5, 3600));

        var now = _timeProvider.GetUtcNow();
        var batch = await _outbox.ClaimDeliveriesAsync(batchSize, now, leaseDuration, leaseOwner, ct).ConfigureAwait(false);

        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await ProcessItemAsync(item, leaseOwner, maxAttempts, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Consent webhook event {EventId} for external system {ExternalSystemId} failed processing ({ErrorType}).",
                    item.EventId,
                    item.ExternalSystemId,
                    ex.GetType().Name);
            }
        }

        var lastPurge = Interlocked.Read(ref _lastPurgeTicks);
        if (now.UtcTicks - lastPurge >= PurgeInterval.Ticks
            && Interlocked.CompareExchange(ref _lastPurgeTicks, now.UtcTicks, lastPurge) == lastPurge)
        {
            try
            {
                await _outbox.PurgeExpiredEventsAsync(now - TimeSpan.FromDays(retentionDays), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Consent event retention purge failed ({ErrorType}).",
                    ex.GetType().Name);
            }
        }

        return batch.Count;
    }

    private async Task ProcessItemAsync(
        PendingConsentEventDelivery item,
        string leaseOwner,
        int maxAttempts,
        CancellationToken ct)
    {
        if (item.Attempts > maxAttempts)
        {
            await DeadLetterAsync(item, leaseOwner, WebhookDeliveryErrorCodes.MaxAttemptsExceeded, ct).ConfigureAwait(false);
            return;
        }

        var system = await _workflowConfig.GetExternalWorkflowSystemAsync(
            new TenantId(item.TenantId),
            item.ExternalSystemId,
            ct).ConfigureAwait(false);

        if (system is null)
        {
            await DeadLetterAsync(item, leaseOwner, WebhookDeliveryErrorCodes.SubscriptionNotFound, ct).ConfigureAwait(false);
            return;
        }

        if (!system.Enabled)
        {
            await DeadLetterAsync(item, leaseOwner, WebhookDeliveryErrorCodes.SubscriptionInactive, ct).ConfigureAwait(false);
            return;
        }

        string? secret = ResolveSecret(system);
        if (string.IsNullOrEmpty(secret))
        {
            await DeadLetterAsync(item, leaseOwner, WebhookDeliveryErrorCodes.SecretMissing, ct).ConfigureAwait(false);
            return;
        }

        var message = new WebhookOutboxMessage(
            Id: item.EventId.ToString("D"),
            Source: WebhookSubscriptionSource.CloudEvent,
            SubscriptionId: system.Id.ToString("D"),
            OwnerTenantId: item.TenantId,
            EventTenantId: item.TenantId,
            EventId: item.EventId.ToString("D"),
            EventType: item.EventType,
            EventSource: ConsentEventTypes.EventSource,
            EventTime: item.CreatedAt,
            PayloadJson: item.PayloadJson,
            Attempt: item.Attempts,
            MaxAttempts: maxAttempts,
            State: WebhookOutboxState.InFlight,
            NextAttemptAt: item.NextAttemptAt,
            LeaseUntil: null,
            LeaseOwner: leaseOwner,
            LastStatusCode: null,
            LastErrorCode: null,
            CreatedAt: item.CreatedAt);

        var target = new ResolvedWebhookTarget(system.WebhookUrl, secret, null, system.Enabled);
        var result = await _client.DeliverAsync(message, target, ct).ConfigureAwait(false);
        var decision = result.Decision;

        switch (decision.Outcome)
        {
            case WebhookRetryOutcome.Success:
                await _outbox.MarkDeliveredAsync(
                    item.EventId,
                    item.ExternalSystemId,
                    leaseOwner,
                    _timeProvider.GetUtcNow(),
                    ct).ConfigureAwait(false);
                DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                return;

            case WebhookRetryOutcome.Permanent:
                await DeadLetterAsync(
                    item,
                    leaseOwner,
                    decision.ErrorCode ?? WebhookDeliveryErrorCodes.Unknown,
                    ct).ConfigureAwait(false);
                return;

            default:
                if (decision.ErrorCode is null)
                {
                    return;
                }

                if (item.Attempts >= maxAttempts)
                {
                    await DeadLetterAsync(item, leaseOwner, WebhookDeliveryErrorCodes.MaxAttemptsExceeded, ct).ConfigureAwait(false);
                    return;
                }

                var nextAttemptAt = _timeProvider.GetUtcNow() + _retryPolicy.ComputeDelay(item.Attempts, decision.RetryAfter);
                await _outbox.RescheduleDeliveryAsync(
                    item.EventId,
                    item.ExternalSystemId,
                    leaseOwner,
                    nextAttemptAt,
                    ct).ConfigureAwait(false);
                DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
                return;
        }
    }

    private string? ResolveSecret(ExternalWorkflowSystem system)
    {
        var stored = system.WebhookSecretProtected;
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (stored.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            if (_secretProtector is null)
            {
                return null;
            }

            return _secretProtector.Unprotect(system.TenantId, system.Id.ToString("D"), stored);
        }

        return _secretProtector is null ? stored : null;
    }

    private async Task DeadLetterAsync(
        PendingConsentEventDelivery item,
        string leaseOwner,
        string errorCode,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        await _outbox.MarkDeadLetteredAsync(
            item.EventId,
            item.ExternalSystemId,
            leaseOwner,
            now,
            ct).ConfigureAwait(false);
        DeliveriesCounter.Add(1, new KeyValuePair<string, object?>("outcome", "dead"));
        DeadLettersCounter.Add(1, new KeyValuePair<string, object?>("code", errorCode));
        _logger.LogWarning(
            "Consent webhook event {EventId} for external system {ExternalSystemId} dead-lettered ({ErrorCode}) after {Attempts} attempt(s).",
            item.EventId,
            item.ExternalSystemId,
            errorCode,
            item.Attempts);

        if (_auditWriter is not null)
        {
            var tenant = TenantId.TryParse(item.TenantId, out var parsedTenant) ? parsedTenant : TenantId.Platform;
            var detailsJson = JsonSerializer.Serialize(new
            {
                channel = "consent_event_outbox",
                eventId = item.EventId,
                externalSystemId = item.ExternalSystemId,
                errorCode,
                attempts = item.Attempts
            });
            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = now.ToUniversalTime(),
                EventType = AuditEventTypes.WebhookDeliveryFailed,
                ActorSid = new Sid("SYSTEM:WEBHOOKS"),
                TargetTable = "consent_event_deliveries",
                Decision = "FAILED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = detailsJson,
                TenantId = tenant
            }, ct).ConfigureAwait(false);
        }
    }
}
