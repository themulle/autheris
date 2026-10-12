namespace Autheris.Application.Catalog.Search;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Asynchroner Hintergrund-Dienst zum Aufwärmen des Katalog-Suchindex beim Serverstart.
/// Verhindert Latenzspitzen beim ersten Zugriff durch KI-Agenten oder Anwender.
/// </summary>
public sealed class CatalogSearchWarmupService : BackgroundService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ICatalogSearchEngine _searchEngine;
    private readonly IOptions<CatalogSearchOptions> _options;
    private readonly ILogger<CatalogSearchWarmupService> _logger;

    public CatalogSearchWarmupService(
        ITableMetadataRepository metadataRepository,
        ICatalogSearchEngine searchEngine,
        IOptions<CatalogSearchOptions> options,
        ILogger<CatalogSearchWarmupService> logger)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.WarmupOnStartup || !_options.Value.Enabled) return;

        try
        {
            _logger.LogInformation("Asynchroner Warmup des Katalog-Suchindex gestartet...");
            var tables = await _metadataRepository.GetAllTablesAsync(stoppingToken).ConfigureAwait(false);
            await _searchEngine.RebuildIndexAsync(tables, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Katalog-Suchindex Warmup erfolgreich beendet.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Katalog-Suchindex Warmup abgebrochen (Shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kritischer Fehler beim Warmup des Katalog-Suchindex.");
        }
    }
}
