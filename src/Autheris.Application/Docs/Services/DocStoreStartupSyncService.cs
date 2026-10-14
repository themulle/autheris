namespace Autheris.Application.Docs.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that scans and indexes existing documents in the universal document store
/// upon gateway startup to ensure instant air-gapped search availability.
/// </summary>
public sealed class DocStoreStartupSyncService : IHostedService
{
    private readonly IUniversalDocStore _docStore;
    private readonly ILogger<DocStoreStartupSyncService> _logger;

    public DocStoreStartupSyncService(
        IUniversalDocStore docStore,
        ILogger<DocStoreStartupSyncService> logger)
    {
        _docStore = docStore ?? throw new ArgumentNullException(nameof(docStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting initial document store sync from filesystem...");
            var count = await _docStore.SyncExistingDocumentsAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Initial document store sync finished: {Count} document sections indexed.", count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial document store sync encountered an error, continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
