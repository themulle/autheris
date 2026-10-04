namespace Autheris.Extensions.Dbt;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DbtWebhookReceiver : IDbtWebhookReceiver
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly IDbtHealthCircuitBreaker _circuitBreaker;
    private readonly ILogger<DbtWebhookReceiver> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DbtWebhookReceiver(
        IOptions<GatewayOptions> options,
        IDbtHealthCircuitBreaker circuitBreaker,
        ILogger<DbtWebhookReceiver> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool ValidateSignature(string payload, string? signatureHeader, string secret)
    {
        if (_options.Value.Dbt.danger_bypass_webhook_signature_validation ||
            _options.Value.IsWebhookSignatureBypassed)
        {
            _logger.LogWarning("Dbt webhook signature validation is bypassed by dangerous configuration.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var cleanHeader = signatureHeader.Trim();
        if (cleanHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            cleanHeader = cleanHeader[7..];
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(cleanHeader);
        }
        catch
        {
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(payloadBytes);

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedBytes);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> ProcessedWebhookEvents = new(StringComparer.Ordinal);

    public Task<DbtWebhookProcessingResult> ProcessWebhookAsync(
        string payload,
        string? signatureHeader,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var secret = !string.IsNullOrWhiteSpace(_options.Value.Dbt.WebhookSecret)
            ? _options.Value.Dbt.WebhookSecret
            : Environment.GetEnvironmentVariable("DBT_WEBHOOK_SECRET") ?? string.Empty;

        if (!ValidateSignature(payload, signatureHeader, secret))
        {
            _logger.LogWarning("Unauthorized dbt Cloud webhook call: Invalid HMAC signature.");
            return Task.FromResult(new DbtWebhookProcessingResult(false, "Invalid or missing HMAC signature."));
        }

        DbtCloudWebhookEvent? webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<DbtCloudWebhookEvent>(payload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize dbt Cloud webhook payload.");
            return Task.FromResult(new DbtWebhookProcessingResult(false, $"Malformed payload: {ex.Message}"));
        }

        if (webhookEvent == null)
        {
            return Task.FromResult(new DbtWebhookProcessingResult(false, "Empty webhook payload."));
        }

        bool ignoreTimestampTolerance = _options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            // SEC: replay protection is mandatory – timestamp and event ID must be present (fail-closed).
            if (!webhookEvent.Timestamp.HasValue || string.IsNullOrWhiteSpace(webhookEvent.EventId))
            {
                _logger.LogWarning("Rejecting dbt Cloud webhook: mandatory 'timestamp' or 'eventId' is missing (replay protection).");
                return Task.FromResult(new DbtWebhookProcessingResult(false, "Missing mandatory 'timestamp' or 'eventId' (replay protection)."));
            }

            var skew = Math.Abs((DateTimeOffset.UtcNow - webhookEvent.Timestamp.Value).TotalMinutes);
            if (skew > 5)
            {
                _logger.LogWarning("Rejecting dbt Cloud webhook: event timestamp is skewed or outside acceptable replay window ({Skew:F1} minutes).", skew);
                return Task.FromResult(new DbtWebhookProcessingResult(false, "Event timestamp is skewed or outside acceptable replay window."));
            }
        }

        if (!string.IsNullOrWhiteSpace(webhookEvent.EventId))
        {
            if (!ProcessedWebhookEvents.TryAdd(webhookEvent.EventId, DateTimeOffset.UtcNow))
            {
                _logger.LogInformation("dbt Cloud webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.EventId);
                return Task.FromResult(new DbtWebhookProcessingResult(true, $"Duplicate event '{webhookEvent.EventId}' skipped.", webhookEvent.EventType, webhookEvent.Data?.RunId));
            }

            if (ProcessedWebhookEvents.Count > 10_000)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
                foreach (var (k, v) in ProcessedWebhookEvents)
                {
                    if (v < cutoff)
                    {
                        ProcessedWebhookEvents.TryRemove(k, out _);
                    }
                }
            }
        }

        var runId = webhookEvent.Data?.RunId;
        var runStatus = webhookEvent.Data?.RunStatus ?? "Unknown";
        var eventType = webhookEvent.EventType ?? "job_run.completed";

        _logger.LogInformation("Received dbt Cloud webhook event '{EventType}' for run {RunId} with status '{Status}'.",
            eventType, runId, runStatus);

        return Task.FromResult(new DbtWebhookProcessingResult(
            Success: true,
            Message: $"Successfully processed dbt Cloud run {runId} status '{runStatus}'.",
            EventType: eventType,
            RunId: runId
        ));
    }
}
