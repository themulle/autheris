namespace Autheris.Infrastructure.Usage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Microsoft.Extensions.Hosting;

/// <summary>Seeds initial tariffs from configuration on startup if USAGE_TARIFF has no row for the pair.</summary>
public sealed class UsageInitialTariffSeedService : IHostedService
{
    private readonly IUsageTariffService _tariffService;

    public UsageInitialTariffSeedService(IUsageTariffService tariffService)
    {
        _tariffService = tariffService ?? throw new ArgumentNullException(nameof(tariffService));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _tariffService.SeedInitialTariffsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
