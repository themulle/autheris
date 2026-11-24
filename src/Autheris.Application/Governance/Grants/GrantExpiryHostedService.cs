namespace Autheris.Application.Governance.Grants;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Common;
using Autheris.Application.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodic background sweep (default every 5 minutes, distributed lock guarded) that sends staged
/// grant expiry reminders (tenant <c>ReminderDays</c>, each stage at most once), writes immutable EXPIRED ledger entries once grants pass <c>valid_until</c>,
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
    private readonly IAccessPolicySettingsProvider? _policyProvider;
    private readonly ILogger<GrantExpiryHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _scanInterval;

    public GrantExpiryHostedService(
        IGrantExpiryStore store,
        IConsentWorkflowRouter workflowRouter,
        ILogger<GrantExpiryHostedService> logger,
        IDistributedClusterStateProvider? clusterState = null,
        TimeProvider? timeProvider = null,
        TimeSpan? scanInterval = null,
        IAccessPolicySettingsProvider? policyProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflowRouter = workflowRouter ?? throw new ArgumentNullException(nameof(workflowRouter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _scanInterval = scanInterval ?? DefaultScanInterval;
        _policyProvider = policyProvider;
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

        var reminderCache = new Dictionary<TenantId, IReadOnlyList<int>>();
        var candidates = await _store.GetGrantsWithPendingReminderAsync(
            now, now.AddDays(MaxReminderDays), MaxReminderStages, ct).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (candidate.Consent.ValidTo <= now)
            {
                continue;
            }

            if (!reminderCache.TryGetValue(candidate.Consent.TenantId, out var reminderDays))
            {
                reminderDays = await ResolveReminderDaysAsync(candidate.Consent.TenantId, ct).ConfigureAwait(false);
                reminderCache[candidate.Consent.TenantId] = reminderDays;
            }

            var stage = NextReminderStage(now, candidate.Consent.ValidTo, candidate.Consent.ExpiryNoticeStage, reminderDays);
            if (stage > 0 && await _store.MarkReminderStageSentAsync(candidate, stage, now, ct).ConfigureAwait(false))
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

    /// <summary>Upper bound for a reminder lead time in days.</summary>
    internal const int MaxReminderDays = 365;

    /// <summary>Upper bound for the number of reminder stages per grant.</summary>
    internal const int MaxReminderStages = 10;

    private static readonly int[] DefaultReminderDays = [30, 7];

    private async Task<IReadOnlyList<int>> ResolveReminderDaysAsync(TenantId tenant, CancellationToken ct)
    {
        if (_policyProvider is null)
        {
            return DefaultReminderDays;
        }

        try
        {
            var policy = await _policyProvider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false);
            return policy.ReminderDays;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable policy never suppresses reminders; fall back to the deployment default.
            _logger.LogWarning(ex, "Reminder policy for tenant could not be read; using defaults.");
            return DefaultReminderDays;
        }
    }

    /// <summary>
    /// Returns the reminder stage to send now, or 0 when none is due. Stages are the distinct reminder lead times in
    /// descending order (stage 1 is the earliest reminder). When several stages are overdue only the most imminent one is
    /// returned, so a late sweep never sends a burst of stale reminders. A stage is sent at most once.
    /// </summary>
    internal static int NextReminderStage(
        DateTimeOffset now,
        DateTimeOffset validTo,
        int sentStage,
        IReadOnlyList<int> reminderDays)
    {
        if (now >= validTo || reminderDays is null || reminderDays.Count == 0)
        {
            return 0;
        }

        var stages = new List<int>(reminderDays.Count);
        foreach (var d in reminderDays)
        {
            if (d is >= 1 and <= MaxReminderDays && !stages.Contains(d))
            {
                stages.Add(d);
            }
        }

        stages.Sort((a, b) => b.CompareTo(a));
        if (stages.Count > MaxReminderStages)
        {
            stages.RemoveRange(MaxReminderStages, stages.Count - MaxReminderStages);
        }

        var due = 0;
        for (var i = 0; i < stages.Count; i++)
        {
            if (now >= validTo - TimeSpan.FromDays(stages[i]))
            {
                due = i + 1;
            }
        }

        return due > sentStage ? due : 0;
    }
}
