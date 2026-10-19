using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Audit;

/// <summary>
/// SEC-AUD161-01 / SEC-AUD161-02 / SEC-AUD161-03: Fenced background worker that periodically purges
/// expired audit entries older than configured retention floors, maintaining tamper-evident cryptographic continuity.
/// </summary>
public sealed class AuditRetentionPurgeWorker : BackgroundService
{
    public const string LeaseKey = "autheris:lease:audit-retention-purge";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly IOptionsMonitor<AuditRetentionOptions> _optionsMonitor;
    private readonly IAuditWriter _auditWriter;
    private readonly IAuditChainAnchorStore _anchorStore;
    private readonly IAuditPurgeKeyProvider? _keyProvider;
    private readonly IAuditAnchorSigner? _externalSigner;
    private readonly ILogger<AuditRetentionPurgeWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public AuditRetentionPurgeWorker(
        IServiceScopeFactory scopeFactory,
        IDistributedClusterStateProvider clusterState,
        IOptionsMonitor<AuditRetentionOptions> optionsMonitor,
        IAuditWriter auditWriter,
        IAuditChainAnchorStore anchorStore,
        ILogger<AuditRetentionPurgeWorker> logger,
        IAuditPurgeKeyProvider? keyProvider = null,
        IAuditAnchorSigner? externalSigner = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clusterState = clusterState ?? throw new ArgumentNullException(nameof(clusterState));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _anchorStore = anchorStore ?? throw new ArgumentNullException(nameof(anchorStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyProvider = keyProvider;
        _externalSigner = externalSigner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            var interval = options.PurgeInterval <= TimeSpan.Zero ? TimeSpan.FromHours(24) : options.PurgeInterval;

            try
            {
                if (options.Enabled)
                {
                    await RunPurgeCycleWithLeaseAsync(options, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogDebug("Audit retention purge job is disabled; skipping cycle.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred during audit retention purge cycle.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RunPurgeCycleWithLeaseAsync(AuditRetentionOptions options, CancellationToken stoppingToken)
    {
        var lockTimeout = options.LockTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : options.LockTimeout;
        IDistributedLease? lease;
        try
        {
            lease = await _clusterState.TryAcquireLeaseAsync(LeaseKey, lockTimeout, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire distributed lease {LeaseKey} for audit retention purge.", LeaseKey);
            return;
        }

        if (lease == null)
        {
            _logger.LogDebug("Could not acquire lease {LeaseKey}; another instance is executing the purge cycle.", LeaseKey);
            return;
        }

        await using (lease.ConfigureAwait(false))
        {
            using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);
            var ct = cycleCts.Token;

            try
            {
                await ExecutePurgeCycleAsync(options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.LeaseLost.IsCancellationRequested)
            {
                _logger.LogWarning("Audit retention purge cycle aborted: distributed lease was lost or expired.");
            }
        }
    }

    public async Task ExecutePurgeCycleAsync(AuditRetentionOptions options, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var keyProvider = _keyProvider ?? scope.ServiceProvider.GetService<IAuditPurgeKeyProvider>();
        var hmacKey = keyProvider?.GetAuditHmacKey();
        if (hmacKey == null || hmacKey.Length == 0)
        {
            _logger.LogError("Audit HMAC key unavailable; cannot generate cryptographic purge checkpoint.");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var thresholdUtc = now - TimeSpan.FromDays(options.RetentionDays);

        var plan = await repo.PlanPurgePrefixAsync(thresholdUtc, ct).ConfigureAwait(false);
        if (plan == null || plan.Value.PurgeCount <= 0)
        {
            _logger.LogDebug("No audit entries eligible for retention purge at threshold {ThresholdUtc}.", thresholdUtc);
            return;
        }

        var (maxEligibleSeq, firstSurvivingSeq, survivingPrevHash, totalEligibleCount) = plan.Value;
        var createdAtUtc = _timeProvider.GetUtcNow();

        var unsignedCheckpoint = new AuditPurgeCheckpoint(
            firstSurvivingSeq,
            survivingPrevHash,
            totalEligibleCount,
            thresholdUtc,
            createdAtUtc,
            string.Empty,
            null);

        var signature = AuditChainEngine.ComputePurgeCheckpointSignature(hmacKey, unsignedCheckpoint);
        var checkpoint = unsignedCheckpoint with { Signature = signature };

        if (_externalSigner != null && checkpoint.ExternalSignature == null)
        {
            var payload = AuditChainEngine.BuildPurgeCheckpointPayload(checkpoint);
            var extSig = _externalSigner.Sign(Encoding.UTF8.GetBytes(payload));
            checkpoint = checkpoint with { ExternalSignature = extSig };
        }

        // Save purge checkpoint anchor prior to batch deletion (AC-161-04)
        _anchorStore.SavePurgeCheckpoint(checkpoint);
        var storedCheckpoint = _anchorStore.LoadPurgeCheckpoint() ?? checkpoint;
        await repo.SavePurgeCheckpointAsync(storedCheckpoint, ct).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        int batchSize = options.BatchSize <= 0 ? 5000 : options.BatchSize;
        long totalPurged = 0;

        while (totalPurged < totalEligibleCount && !ct.IsCancellationRequested)
        {
            int deleted = await repo.ExecutePurgeBatchAsync(maxEligibleSeq, batchSize, ct).ConfigureAwait(false);
            if (deleted <= 0)
            {
                break;
            }
            totalPurged += deleted;

            if (options.InterBatchDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(options.InterBatchDelay, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        stopwatch.Stop();

        // Write Tier A fail-closed audit entry with strict Zero-Payload rule (AC-161-07, AC-161-08)
        var auditEntry = new AuditLogEntry
        {
            EventType = AuditEventTypes.AuditRetentionPurge,
            OccurredAt = _timeProvider.GetUtcNow(),
            Decision = "PURGED",
            ActorSid = new Sid("S-1-5-18"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                purgedCount = totalPurged,
                firstSurvivingSequence = firstSurvivingSeq,
                lastPurgedSequence = maxEligibleSeq,
                thresholdUtc = thresholdUtc.ToString("O"),
                durationMs = stopwatch.ElapsedMilliseconds
            })
        };

        try
        {
            await _auditWriter.RecordRequiredAsync(auditEntry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record Tier A self-audit entry for audit retention purge.");
            throw;
        }

        _logger.LogInformation(
            "Audit retention purge completed: {PurgedCount} rows purged in {DurationMs}ms (first surviving seq {FirstSeq}).",
            totalPurged, stopwatch.ElapsedMilliseconds, firstSurvivingSeq);
    }
}
