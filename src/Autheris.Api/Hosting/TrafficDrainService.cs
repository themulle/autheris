using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Hosting;

public sealed class TrafficDrainController : ITrafficDrainController
{
    private volatile bool _isDraining;
    private int _activeQueryCount;
    private readonly TaskCompletionSource _drainCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsDraining => _isDraining;
    public int ActiveQueryCount => Volatile.Read(ref _activeQueryCount);

    public void InitiateGracefulShutdown()
    {
        _isDraining = true;
    }

    public void MarkCompleted()
    {
        _drainCompleted.TrySetResult();
    }

    public IDisposable TrackQuery()
    {
        Interlocked.Increment(ref _activeQueryCount);
        return new QueryScope(this);
    }

    public async Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (Volatile.Read(ref _activeQueryCount) > 0 && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WaitForCompletionAsync() => _drainCompleted.Task;

    private sealed class QueryScope(TrafficDrainController controller) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref controller._activeQueryCount);
            }
        }
    }
}

public sealed class TrafficDrainHostedService : IHostedService
{
    private readonly ITrafficDrainController _controller;
    private readonly HighAvailabilityOptions _haOptions;
    private readonly ILogger<TrafficDrainHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public TrafficDrainHostedService(
        ITrafficDrainController controller,
        IOptions<GatewayOptions> options,
        ILogger<TrafficDrainHostedService> logger,
        IHostApplicationLifetime lifetime)
    {
        _controller = controller;
        _haOptions = options.Value.HighAvailability;
        _logger = logger;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TrafficDrainHostedService started. Node is ready for traffic.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("NF-HA-01 Graceful Shutdown eingeleitet. Phase 1: Status auf Draining gesetzt.");
        _controller.InitiateGracefulShutdown();

        _logger.LogWarning("NF-HA-01 Phase 2: /health/ready liefert ab sofort HTTP 503.");

        // Phase 3: Drain-Puffer für Load Balancer Deregistrierung
        var drainDelay = TimeSpan.FromSeconds(_haOptions.DrainDelaySeconds);
        _logger.LogInformation("NF-HA-01 Phase 3: Warte {Delay}s auf Load-Balancer-Propagation.", drainDelay.TotalSeconds);
        try
        {
            await Task.Delay(drainDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Drain-Puffer durch CancellationToken abgebrochen.");
        }

        _logger.LogInformation("NF-HA-01 Phase 4 & 5: Kestrel schließt Keep-Alive-Verbindungen und wartet auf In-Flight GraphQL Queries (Timeout: {Timeout}s).", _haOptions.QueryTimeoutSeconds);

        var queryTimeout = TimeSpan.FromSeconds(_haOptions.ShutdownTimeoutSeconds > 0 ? _haOptions.ShutdownTimeoutSeconds : _haOptions.QueryTimeoutSeconds);
        try
        {
            await _controller.WaitForCompletionAsync(queryTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Warten auf In-Flight-Queries durch CancellationToken abgebrochen.");
        }

        _controller.MarkCompleted();
        _logger.LogInformation("NF-HA-01 Phase 6: Shutdown erfolgreich abgeschlossen.");
    }
}
