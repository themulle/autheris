namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Interfaces;

/// <summary>
/// Database-backed catalog version provider reading (structure_version, content_version) from CATALOG_STATE
/// with short TTL caching and event-driven cache invalidation.
/// </summary>
public sealed class DbCatalogVersionProvider : ICatalogVersionProvider, IDisposable
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IEventBus? _eventBus;
    private readonly TimeSpan _ttl;
    private readonly IDisposable? _eventSubscription;

    private CatalogVersion? _cachedVersion;
    private long _cachedAtTicks;
    private readonly Lock _lock = new();

    public DbCatalogVersionProvider(
        ITableMetadataRepository metadataRepository,
        IEventBus? eventBus = null,
        TimeSpan? ttl = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _eventBus = eventBus;
        _ttl = ttl ?? TimeSpan.FromSeconds(5);

        if (_eventBus != null)
        {
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    Invalidate();
                    return Task.CompletedTask;
                });
        }
    }

    public DbCatalogVersionProvider(
        IGovernanceRepository governanceRepository,
        IEventBus? eventBus = null,
        TimeSpan? ttl = null)
        : this((ITableMetadataRepository)(governanceRepository ?? throw new ArgumentNullException(nameof(governanceRepository))), eventBus, ttl)
    {
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedVersion = null;
            _cachedAtTicks = 0;
        }
    }

    public async ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            if (_cachedVersion.HasValue && (nowTicks - _cachedAtTicks < _ttl.Ticks))
            {
                return _cachedVersion.Value;
            }
        }

        var version = await _metadataRepository.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            _cachedVersion = version;
            _cachedAtTicks = DateTime.UtcNow.Ticks;
        }

        return version;
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var version = await GetCatalogVersionAsync(ct).ConfigureAwait(false);
        return version.Formatted;
    }

    public void Dispose()
    {
        _eventSubscription?.Dispose();
    }
}
