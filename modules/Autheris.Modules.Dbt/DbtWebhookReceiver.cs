using Autheris.Application.State;

namespace Autheris.Modules.Dbt;

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.State.Caching;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class DbtWebhookReceiver : IDbtCloudWebhookHandler
{
    internal const int MaxDedupEntries = 10_000;
    internal static readonly TimeSpan ReplaySkewWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DedupTtl = TimeSpan.FromHours(24);
    private const long EvictionLogIntervalTicks = TimeSpan.TicksPerMinute;

    private sealed class DedupState(TimeProvider time)
    {
        public BoundedCache<string, byte> Cache { get; } = new(MaxDedupEntries, DedupTtl, time, StringComparer.Ordinal);
        public object Gate { get; } = new();
        public long LastEvictionLogTicks;
    }

    private static readonly DedupState SharedDedup = new(TimeProvider.System);

    private readonly IOptions<GatewayOptions> _options;
    private readonly IDbtHealthCircuitBreaker _circuitBreaker;
    private readonly ILogger<DbtWebhookReceiver> _logger;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly TimeProvider _time;
    private readonly DedupState _dedup;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DbtWebhookReceiver(
        IOptions<GatewayOptions> options,
        IDbtHealthCircuitBreaker circuitBreaker,
        ILogger<DbtWebhookReceiver> logger,
        IDistributedClusterStateProvider? clusterState = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
        _time = timeProvider ?? TimeProvider.System;
        _dedup = timeProvider is null || ReferenceEquals(timeProvider, TimeProvider.System)
            ? SharedDedup
            : new DedupState(_time);
    }

    internal int ActiveDedupCount => _dedup.Cache.Count;
    internal static int ProcessedWebhookEventsCount => SharedDedup.Cache.Count;
    internal static void ClearProcessedWebhookEvents()
    {
        SharedDedup.Cache.Clear();
        Interlocked.Exchange(ref SharedDedup.LastEvictionLogTicks, 0);
    }

    /// <summary>Handles a payload whose HMAC signature the core webhook route has already verified.</summary>
    public async Task<DbtWebhookProcessingResult> HandleVerifiedAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        if (Encoding.UTF8.GetByteCount(payload) > 2 * 1024 * 1024)
        {
            return new DbtWebhookProcessingResult(false, "Webhook payload exceeds maximum allowed size (2 MB).");
        }

        DbtCloudWebhookEvent? webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<DbtCloudWebhookEvent>(payload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to deserialize dbt Cloud webhook payload ({ExceptionType}).", ex.GetType().Name);
            return new DbtWebhookProcessingResult(false, "Malformed payload.");
        }

        if (webhookEvent == null)
        {
            return new DbtWebhookProcessingResult(false, "Empty webhook payload.");
        }

        bool ignoreTimestampTolerance = _options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            // replay protection is mandatory – timestamp and event ID must be present (fail-closed).
            if (!webhookEvent.Timestamp.HasValue || string.IsNullOrWhiteSpace(webhookEvent.EventId))
            {
                _logger.LogWarning("Rejecting dbt Cloud webhook: mandatory 'timestamp' or 'eventId' is missing (replay protection).");
                return new DbtWebhookProcessingResult(false, "Missing mandatory 'timestamp' or 'eventId' (replay protection).");
            }

            var skew = Math.Abs((_time.GetUtcNow() - webhookEvent.Timestamp.Value).TotalMinutes);
            if (skew > ReplaySkewWindow.TotalMinutes)
            {
                _logger.LogWarning("Rejecting dbt Cloud webhook: event timestamp is skewed or outside acceptable replay window ({Skew:F1} minutes).", skew);
                return new DbtWebhookProcessingResult(false, "Event timestamp is skewed or outside acceptable replay window.");
            }
        }

        if (!string.IsNullOrWhiteSpace(webhookEvent.EventId))
        {
            if (_clusterState != null)
            {
                try
                {
                    var count = await _clusterState.IncrementAsync($"dbt:dedup:{webhookEvent.EventId}", 1, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
                    if (count is null)
                    {
                        if (ignoreTimestampTolerance)
                        {
                            _logger.LogError("dbt webhook cluster state returned null; rejecting webhook while timestamp tolerance is ignored.");
                            return new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.");
                        }

                        _logger.LogWarning("dbt webhook cluster state returned null; falling back to in-memory deduplication.");
                    }
                    else if (count > 1)
                    {
                        _logger.LogInformation("Cluster dbt Cloud webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.EventId);
                        return new DbtWebhookProcessingResult(true, $"Duplicate event '{webhookEvent.EventId}' skipped.", webhookEvent.EventType, webhookEvent.Data?.RunId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ignoreTimestampTolerance)
                    {
                        _logger.LogError(ex, "dbt webhook cluster state unavailable; rejecting webhook while timestamp tolerance is ignored.");
                        return new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.");
                    }

                    _logger.LogWarning(ex, "dbt webhook cluster state unavailable; falling back to in-memory deduplication.");
                }
            }

            if (!ignoreTimestampTolerance || _clusterState == null)
            {
                if (TryRecordOrDetectDuplicate(webhookEvent.EventId))
                {
                    _logger.LogInformation("dbt Cloud webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.EventId);
                    return new DbtWebhookProcessingResult(true, $"Duplicate event '{webhookEvent.EventId}' skipped.", webhookEvent.EventType, webhookEvent.Data?.RunId);
                }
            }
        }

        var runId = webhookEvent.Data?.RunId;
        var runStatus = webhookEvent.Data?.RunStatus ?? "Unknown";
        var eventType = webhookEvent.EventType ?? "job_run.completed";

        _logger.LogInformation("Received dbt Cloud webhook event '{EventType}' for run {RunId} with status '{Status}'.",
            eventType, runId, runStatus);

        return new DbtWebhookProcessingResult(
            Success: true,
            Message: $"Successfully processed dbt Cloud run {runId} status '{runStatus}'.",
            EventType: eventType,
            RunId: runId
        );
    }

    private bool TryRecordOrDetectDuplicate(string eventId)
    {
        bool atCapacity;
        lock (_dedup.Gate)
        {
            if (_dedup.Cache.TryGet(eventId, out _))
            {
                return true;
            }

            atCapacity = _dedup.Cache.Count >= MaxDedupEntries;
            _dedup.Cache.Set(eventId, 1);
        }

        if (atCapacity)
        {
            long nowTicks = _time.GetUtcNow().UtcTicks;
            long prevTicks = Interlocked.Read(ref _dedup.LastEvictionLogTicks);
            if ((prevTicks == 0 || nowTicks - prevTicks >= EvictionLogIntervalTicks) &&
                Interlocked.CompareExchange(ref _dedup.LastEvictionLogTicks, nowTicks, prevTicks) == prevTicks)
            {
                _logger.LogWarning(
                    "dbt Cloud webhook deduplication cache reached capacity ({Capacity}); oldest entries are being evicted.",
                    MaxDedupEntries);
            }
        }

        return false;
    }
}
