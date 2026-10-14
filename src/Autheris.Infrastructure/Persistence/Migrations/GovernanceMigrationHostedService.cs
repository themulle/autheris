using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Migrates and initializes the governance store before any other hosted service starts (it must be registered first).
/// <see cref="StartAsync"/> blocks and throws on failure, so the host does not start with an unmigrated or drifted schema (decision G-8).
/// </summary>
public sealed class GovernanceMigrationHostedService : IHostedService
{
    private readonly IGovernanceMigrationRunner _runner;
    private readonly IGovernanceStoreInitializer _initializer;
    private readonly ILogger<GovernanceMigrationHostedService>? _logger;

    public GovernanceMigrationHostedService(
        IGovernanceMigrationRunner runner,
        IGovernanceStoreInitializer initializer,
        ILogger<GovernanceMigrationHostedService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(initializer);
        _runner = runner;
        _initializer = initializer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runner.RunAsync(cancellationToken).ConfigureAwait(false);
            await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Governance database migration or initialization failed; refusing to start.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
