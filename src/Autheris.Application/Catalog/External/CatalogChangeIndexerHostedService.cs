namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Periodically (every 60 s) and on catalog-change signals hashes each tenant's in-scope catalog objects,
/// updating <c>CATALOG_OBJECT_VERSIONS</c> and appending monotonic rows to <c>CATALOG_CHANGES</c>.
/// </summary>
public sealed class CatalogChangeIndexerHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IDataOwnershipRepository _ownershipRepository;
    private readonly ICatalogChangeStore _changeStore;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IDiscoveryArtifactStore? _artifactStore;
    private readonly ICatalogChangedSignal? _signal;
    private readonly IEventBus? _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<CatalogChangeIndexerHostedService> _logger;
    private IDisposable? _eventSubscription;

    public CatalogChangeIndexerHostedService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IDataOwnershipRepository ownershipRepository,
        ICatalogChangeStore changeStore,
        IOptions<GatewayOptions> gatewayOptions,
        IDiscoveryArtifactStore? artifactStore = null,
        ICatalogChangedSignal? signal = null,
        IEventBus? eventBus = null,
        TimeProvider? timeProvider = null,
        ILogger<CatalogChangeIndexerHostedService>? logger = null,
        TimeSpan? interval = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _ownershipRepository = ownershipRepository ?? throw new ArgumentNullException(nameof(ownershipRepository));
        _changeStore = changeStore ?? throw new ArgumentNullException(nameof(changeStore));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _artifactStore = artifactStore;
        _signal = signal;
        _eventBus = eventBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? NullLogger<CatalogChangeIndexerHostedService>.Instance;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_eventBus is not null && _signal is not null)
        {
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    _signal.Signal();
                    return Task.CompletedTask;
                });
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IndexOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catalog change indexer sweep failed ({ErrorType}); retrying on next tick.", ex.GetType().Name);
            }

            try
            {
                await WaitForNextTriggerAsync(timer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> IndexOnceAsync(CancellationToken ct = default)
    {
        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tenants = await ResolveTargetTenantsAsync(allTables, ct).ConfigureAwait(false);
        int changesWritten = 0;

        foreach (var tenantId in tenants)
        {
            changesWritten += await IndexTenantAsync(tenantId, allTables, ct).ConfigureAwait(false);
        }

        return changesWritten;
    }

    public async Task<int> IndexTenantAsync(
        TenantId tenantId,
        IReadOnlyList<TableMetadata>? preloadedTables = null,
        CancellationToken ct = default)
    {
        var allTables = preloadedTables ?? await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(allTables, tenantId);
        var consentTables = await _consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct).ConfigureAwait(false);
        var consentSet = consentTables as IReadOnlySet<TableIdentifier> ?? consentTables.ToHashSet();
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Value.Catalog.SharedTables);

        var activeTables = virtualFiltered
            .Where(t => t.Table.IsActive && DiscoveryTenantScope.IsInScope(t, tenantId, catalogOwner, sharedTables, consentSet))
            .OrderBy(static t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingVersions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
        var ownersByTable = activeTables.Count > 0
            ? await _ownershipRepository.GetDataOwnersForTablesAsync(activeTables.Select(static t => t.Identifier).ToList(), ct).ConfigureAwait(false)
            : new Dictionary<TableIdentifier, IReadOnlyList<DataOwner>>();

        var upserts = new List<CatalogVersionUpsert>();
        var activeObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in activeTables)
        {
            var objectId = table.Identifier.ToString();
            activeObjectIds.Add(objectId);

            var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<DataOwner>();
            var artifacts = _artifactStore is null
                ? null
                : await _artifactStore.GetAsync(table.Table.Id, ct).ConfigureAwait(false);
            var hash = ExternalCatalogReader.ComputeAdminRepresentationHash(table, owners, artifacts);

            if (!existingVersions.TryGetValue(objectId, out var prev)
                || prev.Removed
                || !string.Equals(prev.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                upserts.Add(new CatalogVersionUpsert(objectId, hash, Removed: false));
            }
        }

        foreach (var prev in existingVersions.Values)
        {
            if (!prev.Removed && !activeObjectIds.Contains(prev.ObjectId))
            {
                upserts.Add(new CatalogVersionUpsert(prev.ObjectId, prev.ContentHash, Removed: true));
            }
        }

        if (upserts.Count == 0)
        {
            return 0;
        }

        await _changeStore.ApplyVersionChangesAsync(tenantId, upserts, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return upserts.Count;
    }

    private async Task<IReadOnlyList<TenantId>> ResolveTargetTenantsAsync(
        IReadOnlyList<TableMetadata> allTables,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TenantId>();

        void AddIfValid(TenantId? tid)
        {
            if (tid is { Value: { Length: > 0 } v } && seen.Add(v))
            {
                result.Add(tid.Value);
            }
        }

        AddIfValid(TenantResolver.EffectiveDefaultOf(_gatewayOptions.Value.Tenancy));

        foreach (var known in await _changeStore.ListKnownTenantIdsAsync(ct).ConfigureAwait(false))
        {
            AddIfValid(known);
        }

        foreach (var t in allTables)
        {
            if (!string.IsNullOrWhiteSpace(t.OwnerTenantId) && TenantId.TryParse(t.OwnerTenantId, out var ownerTenant))
            {
                AddIfValid(ownerTenant);
            }
        }

        return result;
    }

    private async Task WaitForNextTriggerAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        if (_signal is null)
        {
            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tickTask = timer.WaitForNextTickAsync(linkedCts.Token).AsTask();
        var signalTask = _signal.WaitAsync(linkedCts.Token).AsTask();
        await Task.WhenAny(tickTask, signalTask).ConfigureAwait(false);
        await linkedCts.CancelAsync().ConfigureAwait(false);
    }
}
