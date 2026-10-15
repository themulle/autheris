namespace Autheris.Application.Common.Hosting;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Base class for periodic background services using <see cref="PeriodicTimer"/> with uniform
/// cancellation and exception handling.
/// </summary>
public abstract class ScheduledHostedService : BackgroundService
{
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly TimeSpan _initialDelay;

    protected ScheduledHostedService(TimeSpan interval, ILogger logger, TimeSpan initialDelay = default)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        _interval = interval;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _initialDelay = initialDelay;
    }

    protected abstract Task ExecuteTickAsync(CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_initialDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during scheduled tick in {ServiceName}.", GetType().Name);
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
}
