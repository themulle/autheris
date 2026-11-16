namespace Autheris.Application.Governance.Grants;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodic background sweep (default every 5 minutes, distributed lock guarded) that sends single-shot
/// grant expiring notices, writes immutable EXPIRED ledger entries once grants pass <c>valid_until</c>,
/// and expires stale undecided consent requests past <c>request_timeout_days</c>.
/// </summary>
public sealed class GrantExpiryHostedService : BackgroundService
{
    public const string ScanLockKey = "governance:grant-expiry:scan";
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(4);

    private readonly IGrantExpiryStore _store;
    private readonly IConsentWorkflowRouter _workflowRouter;
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly ILogger<GrantExpiryHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _scanInterval;

    public GrantExpiryHostedService(
        IGrantExpiryStore store,
        IConsentWorkflowRouter workflowRouter,
        ILogger<GrantExpiryHostedService> logger,
        IDistributedClusterStateProvider? clusterState = null,
        TimeProvider? timeProvider = null,
        TimeSpan? scanInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflowRouter = workflowRouter ?? throw new ArgumentNullException(nameof(workflowRouter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _scanInterval = scanInterval ?? DefaultScanInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_scanInterval, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepWithLockAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during periodic grant expiry sweep.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<GrantExpirySweepSummary?> RunSweepWithLockAsync(CancellationToken ct)
    {
        IAsyncDisposable? lease = null;
        if (_clusterState is not null)
        {
            lease = await _clusterState.TryAcquireLockAsync(ScanLockKey, LockTtl, ct).ConfigureAwait(false);
            if (lease is null)
            {
                _logger.LogDebug("Skipping grant expiry sweep; distributed lock '{LockKey}' is held by another replica.", ScanLockKey);
                return null;
            }
        }

        try
        {
            return await RunSweepAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<GrantExpirySweepSummary> RunSweepAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var noticesSent = 0;
        var grantsExpired = 0;
        var requestsTimedOut = 0;

        var noticeCandidates = await _store.GetGrantsWithoutExpiryNoticeAsync(now, ct).ConfigureAwait(false);
        foreach (var candidate in noticeCandidates)
        {
            if (candidate.Consent.ValidTo <= now)
            {
                continue;
            }

            var resolution = await _workflowRouter.ResolveForTableAsync(
                candidate.Consent.TenantId,
                candidate.Consent.TableIdentifier,
                ct).ConfigureAwait(false);

            var fireAt = ComputeExpiryNoticeTime(
                candidate.Consent.ValidFrom,
                candidate.Consent.ValidTo,
                resolution.ExpiryNoticeLeadHours);

            if (now >= fireAt && await _store.MarkExpiryNoticeSentAsync(candidate, now, ct).ConfigureAwait(false))
            {
                noticesSent++;
            }
        }

        var expiredGrants = await _store.GetExpiredGrantsWithoutRecordAsync(now, ct).ConfigureAwait(false);
        foreach (var candidate in expiredGrants)
        {
            if (await _store.RecordGrantExpiryAsync(candidate, now, ct).ConfigureAwait(false))
            {
                grantsExpired++;
            }
        }

        var pendingRequests = await _store.GetOpenPendingRequestsAsync(now, ct).ConfigureAwait(false);
        foreach (var request in pendingRequests)
        {
            var resolution = await _workflowRouter.ResolveForTableAsync(
                request.TenantId,
                request.TableIdentifier,
                ct).ConfigureAwait(false);
            var timeoutDays = Math.Max(1, resolution.RequestTimeoutDays);
            if (now >= request.RequestedAt.AddDays(timeoutDays)
                && await _store.ExpirePendingRequestAsync(request, now, ct).ConfigureAwait(false))
            {
                requestsTimedOut++;
            }
        }

        return new GrantExpirySweepSummary(noticesSent, grantsExpired, requestsTimedOut);
    }

    internal static DateTimeOffset ComputeExpiryNoticeTime(
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        int expiryNoticeLeadHours)
    {
        var lead = TimeSpan.FromHours(Math.Max(1, expiryNoticeLeadHours));
        var lifetime = validUntil - validFrom;
        if (lifetime > TimeSpan.Zero && lifetime < TimeSpan.FromTicks(lead.Ticks * 2))
        {
            return validUntil - TimeSpan.FromTicks(lifetime.Ticks / 2);
        }

        return validUntil - lead;
    }
}
