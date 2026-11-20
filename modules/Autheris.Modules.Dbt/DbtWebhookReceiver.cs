using Autheris.Application.State;

namespace Autheris.Modules.Dbt;

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class DbtWebhookReceiver : IDbtCloudWebhookHandler
{
    internal static readonly TimeSpan ReplaySkewWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DedupTtl = TimeSpan.FromMinutes(15);

    private readonly IOptions<GatewayOptions> _options;
    private readonly IDbtHealthCircuitBreaker _circuitBreaker;
    private readonly ILogger<DbtWebhookReceiver> _logger;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly TimeProvider _time;
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
            // Replay protection is mandatory – timestamp and event ID must be present (fail-closed).
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
            if (_clusterState is null)
            {
                _logger.LogError("dbt webhook deduplication store is not configured; rejecting webhook.");
                return new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.", StoreUnavailable: true);
            }

            long? count;
            try
            {
                count = await _clusterState.IncrementAsync($"dbt:dedup:{webhookEvent.EventId}", 1, DedupTtl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "dbt webhook cluster state unavailable; rejecting webhook.");
                return new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.", StoreUnavailable: true);
            }

            if (count is null)
            {
                _logger.LogError("dbt webhook cluster state returned null; rejecting webhook.");
                return new DbtWebhookProcessingResult(false, "Webhook deduplication unavailable.", StoreUnavailable: true);
            }

            if (count > 1)
            {
                _logger.LogInformation("Cluster dbt Cloud webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.EventId);
                return new DbtWebhookProcessingResult(true, $"Duplicate event '{webhookEvent.EventId}' skipped.", webhookEvent.EventType, webhookEvent.Data?.RunId);
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
}
