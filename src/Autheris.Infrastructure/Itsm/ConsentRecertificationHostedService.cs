namespace Autheris.Infrastructure.Itsm;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background worker checking for expiring temporary consents and triggering recertification workflows.
/// </summary>
public sealed class ConsentRecertificationHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConsentRecertificationHostedService> _logger;
    private readonly TimeSpan _checkInterval;

    public ConsentRecertificationHostedService(
        IServiceProvider serviceProvider,
        ILogger<ConsentRecertificationHostedService> logger,
        TimeSpan? checkInterval = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _checkInterval = checkInterval ?? TimeSpan.FromHours(4);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Consent Recertification hosted service started. Interval: {Interval}h", _checkInterval.TotalHours);

        using var timer = new PeriodicTimer(_checkInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var recertificationService = scope.ServiceProvider.GetService<IConsentRecertificationService>();
                if (recertificationService != null)
                {
                    await recertificationService.ScanAndTriggerExpiringConsentRecertificationsAsync(ct: stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing consent recertification scan: {Message}", ex.Message);
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

        _logger.LogInformation("Consent Recertification hosted service stopped.");
    }
}
