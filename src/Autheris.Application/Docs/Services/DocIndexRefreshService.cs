namespace Autheris.Application.Docs.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Periodically refreshes the in-memory documentation search index from <see cref="IDocumentRepository"/>,
/// purges expired soft-delete tombstones hourly, and exposes initial load readiness so MCP doc tools fail closed
/// until the first index synchronization succeeds.
/// </summary>
public sealed class DocIndexRefreshService : BackgroundService, IDocIndexReadiness
{
    private static readonly TimeSpan WatermarkOverlap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TombstonePurgeInterval = TimeSpan.FromHours(1);

    private readonly IDocumentRepository _repository;
    private readonly IDocSearchService _searchService;
    private readonly IDocIngestor _ingestor;
    private readonly ILogger<DocIndexRefreshService> _logger;
    private readonly DocsOptions _options;
    private readonly TimeProvider _timeProvider;

    private volatile bool _isInitialLoadComplete;
    private DateTimeOffset _lastSeen;
    private DateTimeOffset _lastTombstonePurge;

    public DocIndexRefreshService(
        IDocumentRepository repository,
        IDocSearchService searchService,
        IDocIngestor ingestor,
        ILogger<DocIndexRefreshService> logger,
        IOptions<DocsOptions>? docsOptions = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = docsOptions?.Value ?? new DocsOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = _timeProvider.GetUtcNow();
        _lastSeen = now;
        _lastTombstonePurge = now;
    }

    public bool IsInitialLoadComplete => _isInitialLoadComplete;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await TryInitialLoadAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RunRefreshTickAsync(CancellationToken ct = default)
    {
        if (!_isInitialLoadComplete)
        {
            return await TryInitialLoadAsync(ct).ConfigureAwait(false);
        }

        var tickStarted = _timeProvider.GetUtcNow();
        var since = _lastSeen - WatermarkOverlap;
        var changed = await _repository.ReadChangedSinceAsync(since, ct).ConfigureAwait(false);
        foreach (var record in changed)
        {
            var key = new DocKey(record.TenantId, record.AppId, record.Category, record.DocId);
            await _searchService.RemoveDocumentAsync(key, ct).ConfigureAwait(false);
            if (!record.IsDeleted)
            {
                var chunks = await BuildChunksAsync(record, ct).ConfigureAwait(false);
                if (chunks.Count > 0)
                {
                    await _searchService.IndexChunksAsync(chunks, ct).ConfigureAwait(false);
                }
            }
        }

        _lastSeen = tickStarted;

        if (tickStarted - _lastTombstonePurge >= TombstonePurgeInterval)
        {
            var cutoff = tickStarted - TimeSpan.FromHours(_options.TombstoneRetentionHours);
            var purged = await _repository.PurgeTombstonesAsync(cutoff, ct).ConfigureAwait(false);
            _lastTombstonePurge = tickStarted;
            if (purged > 0)
            {
                _logger.LogInformation("Purged {Count} expired documentation tombstones.", purged);
            }
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IndexRefreshSeconds));
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunRefreshTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Documentation index refresh tick failed.");
            }
        }
    }

    private async Task<bool> TryInitialLoadAsync(CancellationToken ct)
    {
        try
        {
            var started = _timeProvider.GetUtcNow();
            var records = await _repository.ReadChangedSinceAsync(since: null, ct).ConfigureAwait(false);
            var allChunks = new List<DocChunk>();
            foreach (var record in records)
            {
                if (record.IsDeleted)
                {
                    continue;
                }

                allChunks.AddRange(await BuildChunksAsync(record, ct).ConfigureAwait(false));
            }

            await _searchService.ReplaceAllAsync(allChunks, ct).ConfigureAwait(false);
            _lastSeen = started;
            _isInitialLoadComplete = true;
            _logger.LogInformation("Initial documentation index load succeeded with {Sections} sections.", allChunks.Count);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial documentation index load failed; doc tools remain fail-closed until retry succeeds.");
            return false;
        }
    }

    private Task<IReadOnlyList<DocChunk>> BuildChunksAsync(DocRecord record, CancellationToken ct) =>
        GovernanceDocStore.BuildChunksAsync(_ingestor, record, ct);
}
