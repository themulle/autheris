using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog;

/// <summary>
/// Maintains an immutable in-memory <see cref="CatalogSnapshot"/> updated monotonically.
/// </summary>
public sealed class CatalogSnapshotProvider : ICatalogSnapshotProvider, IDisposable
{
    private readonly IGovernanceRepository _governanceRepo;
    private readonly ICatalogVersionProvider _versionProvider;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<CatalogSnapshotProvider>? _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private CatalogSnapshot _currentSnapshot;
    private IDisposable? _eventSubscription;
    private bool _initialized;
    private bool _disposed;

    public CatalogSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public CatalogSnapshotProvider(
        IGovernanceRepository governanceRepo,
        ICatalogVersionProvider versionProvider,
        IEventBus? eventBus = null,
        ILogger<CatalogSnapshotProvider>? logger = null)
    {
        _governanceRepo = governanceRepo ?? throw new ArgumentNullException(nameof(governanceRepo));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _eventBus = eventBus;
        _logger = logger;
        _currentSnapshot = CatalogSnapshot.Empty();

        if (_eventBus != null)
        {
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                async _ =>
                {
                    try
                    {
                        await RefreshAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Background refresh of catalog snapshot triggered by event failed.");
                    }
                });
        }
    }

    public async ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!_initialized)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        return Volatile.Read(ref _currentSnapshot);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dbVersion = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            var current = Volatile.Read(ref _currentSnapshot);

            // Monotonicity guard: ignore stale or already-up-to-date versions
            if (_initialized)
            {
                if (dbVersion.StructureVersion < current.Version.StructureVersion ||
                    dbVersion.ContentVersion < current.Version.ContentVersion)
                {
                    _logger?.LogWarning(
                        "Detected stale catalog version {NewStructure}.{NewContent} when current is {CurStructure}.{CurContent}; rejecting snapshot refresh to protect monotonicity.",
                        dbVersion.StructureVersion, dbVersion.ContentVersion,
                        current.Version.StructureVersion, current.Version.ContentVersion);
                    return;
                }

                if (dbVersion.StructureVersion == current.Version.StructureVersion &&
                    dbVersion.ContentVersion == current.Version.ContentVersion)
                {
                    return;
                }
            }

            int degradedCount = 0;
            IReadOnlyList<TableMetadata> tables;
            try
            {
                tables = await _governanceRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load tables during catalog snapshot refresh; retaining existing snapshot.");
                return;
            }

            IReadOnlyList<TableRelation> relations;
            try
            {
                relations = await _governanceRepo.GetAllRelationsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load relations during catalog snapshot refresh; retaining existing snapshot.");
                return;
            }

            var newSnapshot = CatalogSnapshot.Create(
                dbVersion,
                tables,
                relations,
                degradedEntityCount: degradedCount,
                capturedAt: DateTimeOffset.UtcNow);

            Volatile.Write(ref _currentSnapshot, newSnapshot);
            _initialized = true;
            _logger?.LogInformation(
                "Catalog snapshot updated to version {StructureVersion}.{ContentVersion} ({TableCount} tables, {RelationCount} relations).",
                dbVersion.StructureVersion, dbVersion.ContentVersion, tables.Count, relations.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _eventSubscription?.Dispose();
            _refreshLock.Dispose();
        }
    }
}
