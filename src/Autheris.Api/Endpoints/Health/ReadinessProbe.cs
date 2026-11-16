namespace Autheris.Api.Endpoints.Health;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Process-wide single-flight readiness dependency probe with a fixed 2-second cache window
/// and a fixed 5-second shared cancellation timeout. Only the dependency report is cached;
/// draining, warm-up, executor gate, and HTTP response bodies are evaluated per request.
/// </summary>
public sealed class ReadinessProbe
{
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly GatewayHealthReport TimedOutReport = new(
        false,
        [new HealthCheckComponentResult("ReadinessProbe", false, "Readiness probe timed out (see gateway logs).")]);

    private static readonly GatewayHealthReport FailedReport = new(
        false,
        [new HealthCheckComponentResult("ReadinessProbe", false, "Readiness probe failed (see gateway logs).")]);

    private readonly IGatewayHealthCheckService _healthCheckService;
    private readonly ILogger<ReadinessProbe> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private Task<GatewayHealthReport>? _inFlight;
    private GatewayHealthReport _cachedReport;
    private long _cachedAt;
    private bool _hasCached;

    public ReadinessProbe(
        IGatewayHealthCheckService healthCheckService,
        ILogger<ReadinessProbe> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(healthCheckService);
        ArgumentNullException.ThrowIfNull(logger);

        _healthCheckService = healthCheckService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<GatewayHealthReport>(ct);
        }

        Task<GatewayHealthReport> task;
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                task = _inFlight;
            }
            else if (_hasCached && _timeProvider.GetElapsedTime(_cachedAt, _timeProvider.GetTimestamp()) < CacheWindow)
            {
                return Task.FromResult(_cachedReport);
            }
            else
            {
                var probeCts = new CancellationTokenSource(ProbeTimeout, _timeProvider);
                task = Task.Run(() => RunProbeCoreAsync(probeCts), CancellationToken.None);
                _inFlight = task;
            }
        }

        return task.WaitAsync(ct);
    }

    private async Task<GatewayHealthReport> RunProbeCoreAsync(CancellationTokenSource probeCts)
    {
        GatewayHealthReport report;
        using (probeCts)
        {
            try
            {
                var probeTask = _healthCheckService.CheckHealthAsync(probeCts.Token);
                _ = probeTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                report = await probeTask.WaitAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Readiness dependency probe timed out after {TimeoutSeconds}s.",
                    ProbeTimeout.TotalSeconds);
                report = TimedOutReport;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Readiness dependency probe failed.");
                report = FailedReport;
            }
        }

        lock (_gate)
        {
            _cachedReport = report;
            _cachedAt = _timeProvider.GetTimestamp();
            _hasCached = true;
            _inFlight = null;
        }

        return report;
    }
}
