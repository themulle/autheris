using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Health;

/// <summary>
/// Review E-11: the audit hash chain is verified periodically at runtime (not only when an export is requested).
/// A broken chain is logged as critical and, unless disabled, switches the readiness probe to unhealthy so that the
/// problem is noticed instead of staying hidden until the next manual export. A violation, and a verification that cannot complete,
/// also enqueue a Data Governance notification (a no-op when no webhook is configured; a dispatcher fault never stops the monitor).
/// </summary>
public sealed class AuditChainIntegrityMonitor : BackgroundService
{
    public const string ViolationEventType = "AUDIT_CHAIN_VIOLATION";
    public const string VerificationIncompleteEventType = "AUDIT_CHAIN_VERIFICATION_INCOMPLETE";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IAuditLogRepository _auditRepository;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AuditChainIntegrityMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly IGovernanceNotificationDispatcher _notifications;

    // Chain state: 0 = not verified yet, 1 = intact, 2 = violated. A violation is latched: only a later run that completes and finds
    // the chain intact clears it. A run that throws never overwrites the chain state (an attacker who can make the verification fail
    // must not be able to hide a detected violation); it only sets _lastRunFailed.
    private int _state;
    private int _lastRunFailed;
    private long _lastCheckedUtcTicks;

    public AuditChainIntegrityMonitor(
        IAuditLogRepository auditRepository,
        IOptions<GatewayOptions> options,
        ILogger<AuditChainIntegrityMonitor> logger,
        TimeProvider? timeProvider = null,
        IGovernanceNotificationDispatcher? notificationDispatcher = null)
    {
        _notifications = notificationDispatcher ?? NoOpGovernanceNotificationDispatcher.Instance;
        _auditRepository = auditRepository;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True once a verification found the chain broken. Stays true until a later run finds it intact.</summary>
    public bool IsViolated => Volatile.Read(ref _state) == 2;

    /// <summary>
    /// True when the last run could not complete (e.g. database unreachable); the current chain state is unknown. A violation detected
    /// earlier stays reported by <see cref="IsViolated"/>.
    /// </summary>
    public bool LastRunFailed => Volatile.Read(ref _lastRunFailed) == 1;

    public DateTimeOffset? LastCheckedAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastCheckedUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Runs one verification and updates the state. Public for tests and for an on-demand trigger.</summary>
    public async Task<bool> VerifyNowAsync(CancellationToken ct = default)
    {
        try
        {
            bool intact = await _auditRepository.VerifyAuditHashChainAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastCheckedUtcTicks, _time.GetUtcNow().UtcTicks);
            var wasViolated = Interlocked.Exchange(ref _state, intact ? 1 : 2) == 2;
            Volatile.Write(ref _lastRunFailed, 0);

            if (intact)
            {
                _logger.LogInformation("Audit hash chain verification succeeded.");
            }
            else
            {
                _logger.LogCritical("AUDIT HASH CHAIN VIOLATION: the tamper-evident audit log failed verification. Investigate immediately (see operations runbook).");

                // notify on the transition only: a violation that stays latched is not re-announced on every run
                if (!wasViolated)
                {
                    await NotifyAsync(
                        ViolationEventType,
                        "The audit hash chain failed verification (chain break or tail truncation). Investigate immediately.",
                        ct).ConfigureAwait(false);
                }
            }

            return intact;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The chain state (in particular a latched violation) is kept; only the failure of this run is recorded.
            var alreadyFailing = Interlocked.Exchange(ref _lastRunFailed, 1) == 1;
            _logger.LogError(ex, "Audit hash chain verification could not be completed.");
            if (!alreadyFailing)
            {
                await NotifyAsync(
                    VerificationIncompleteEventType,
                    "The audit hash chain verification could not be completed; the integrity of the audit log is unknown.",
                    ct).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task NotifyAsync(string eventType, string summary, CancellationToken ct)
    {
        try
        {
            // The message is fixed text: it never carries exception text, connection details or audit content.
            await _notifications.DispatchAsync(
                new GovernanceNotificationMessage(
                    eventType,
                    TenantId.Platform.Value,
                    RealmId: string.Empty,
                    SubjectOrGroupId: "audit-log",
                    summary,
                    _time.GetUtcNow()),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The audit chain notification could not be dispatched.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var audit = _options.Value.Audit;
        if (!audit.VerifyHashChainEnabled)
        {
            _logger.LogWarning("Periodic audit hash chain verification is disabled (Audit:VerifyHashChainEnabled=false).");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(audit.VerifyHashChainIntervalHours, 1, 168));

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(interval);
            do
            {
                await VerifyNowAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
