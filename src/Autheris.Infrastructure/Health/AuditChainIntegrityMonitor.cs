using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Health;

/// <summary>
/// Review E-11: the audit hash chain is verified periodically at runtime (not only when an export is requested).
/// A broken chain is logged as critical and, unless disabled, switches the readiness probe to unhealthy so that the
/// problem is noticed instead of staying hidden until the next manual export.
/// </summary>
public sealed class AuditChainIntegrityMonitor : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IAuditLogRepository _auditRepository;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AuditChainIntegrityMonitor> _logger;
    private readonly TimeProvider _time;

    private int _state; // 0 = not verified yet, 1 = intact, 2 = violated, 3 = verification failed with an error
    private long _lastCheckedUtcTicks;

    public AuditChainIntegrityMonitor(
        IAuditLogRepository auditRepository,
        IOptions<GatewayOptions> options,
        ILogger<AuditChainIntegrityMonitor> logger,
        TimeProvider? timeProvider = null)
    {
        _auditRepository = auditRepository;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True once a verification found the chain broken. Stays true until a later run finds it intact.</summary>
    public bool IsViolated => Volatile.Read(ref _state) == 2;

    /// <summary>True when the last run could not complete (e.g. database unreachable); the chain state is unknown.</summary>
    public bool LastRunFailed => Volatile.Read(ref _state) == 3;

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
            Volatile.Write(ref _state, intact ? 1 : 2);

            if (intact)
            {
                _logger.LogInformation("Audit hash chain verification succeeded.");
            }
            else
            {
                _logger.LogCritical("AUDIT HASH CHAIN VIOLATION: the tamper-evident audit log failed verification. Investigate immediately (see operations runbook).");
            }

            return intact;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _state, 3);
            _logger.LogError(ex, "Audit hash chain verification could not be completed.");
            return false;
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
