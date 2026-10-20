namespace Autheris.Application.Governance.Recertification;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class RecertificationGovernorHostedService : BackgroundService
{
    private readonly IRecertificationService _recertificationService;
    private readonly ILogger<RecertificationGovernorHostedService>? _logger;
    private readonly TimeSpan _checkInterval;

    public RecertificationGovernorHostedService(
        IRecertificationService recertificationService,
        ILogger<RecertificationGovernorHostedService>? logger = null,
        TimeSpan? checkInterval = null)
    {
        _recertificationService = recertificationService ?? throw new ArgumentNullException(nameof(recertificationService));
        _logger = logger;
        _checkInterval = checkInterval ?? TimeSpan.FromHours(1);
    }

    public async Task<int> ExecuteCycleAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        _logger?.LogInformation("Executing recertification auto-revocation cycle as of {AsOf}", asOf);
        var revoked = await _recertificationService.ExecuteAutoRevocationCycleAsync(asOf, ct).ConfigureAwait(false);
        if (revoked > 0)
        {
            _logger?.LogWarning("Recertification governor auto-revoked {Count} expired items (fail-closed)", revoked);
        }
        return revoked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteCycleAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error occurred during recertification auto-revocation cycle");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
