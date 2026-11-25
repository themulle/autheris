namespace Autheris.Application.Catalog.External;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Products;
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
    private readonly IContainerOwnership _ownershipRepository;
    private readonly ICatalogChangeStore _changeStore;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IDiscoveryArtifactStore? _artifactStore;
    private readonly ICatalogChangedSignal? _signal;
    private readonly IEventBus? _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<CatalogChangeIndexerHostedService> _logger;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly IGovernanceDomainRepository? _domainRepository;
    private readonly IDataProductRepository? _productRepository;
    private readonly ICatalogVisibilityEvaluator _visibilityEvaluator;
    private IDisposable? _eventSubscription;

    public CatalogChangeIndexerHostedService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IContainerOwnership ownershipRepository,
        ICatalogChangeStore changeStore,
        IOptions<GatewayOptions> gatewayOptions,
        IDiscoveryArtifactStore? artifactStore = null,
        ICatalogChangedSignal? signal = null,
        IEventBus? eventBus = null,
        TimeProvider? timeProvider = null,
        ILogger<CatalogChangeIndexerHostedService>? logger = null,
        TimeSpan? interval = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        IGovernanceDomainRepository? domainRepository = null,
        IDataProductRepository? productRepository = null,
        ICatalogVisibilityEvaluator? visibilityEvaluator = null)
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
        _protectionProvider = protectionProvider;
        _domainRepository = domainRepository;
        _productRepository = productRepository;
        _visibilityEvaluator = visibilityEvaluator ?? new CatalogVisibilityEvaluator(gatewayOptions, timeProvider: _timeProvider);
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
        var allTables = await CatalogTablePageReader.ReadAllAsync(
            _metadataRepository,
            new CatalogTableQuery(IncludeInactive: false),
            static t => t.Table.IsActive,
            ct: ct).ConfigureAwait(false);
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
        var allTables = preloadedTables ?? await CatalogTablePageReader.ReadAllAsync(
            _metadataRepository,
            new CatalogTableQuery(IncludeInactive: false),
            static t => t.Table.IsActive,
            ct: ct).ConfigureAwait(false);
        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(allTables, tenantId);
        var inScope = await _visibilityEvaluator.FilterInScopeAsync(virtualFiltered, tenantId, _consentRepository, ct).ConfigureAwait(false);
        var activeTables = new List<TableMetadata>(inScope);
        var inScopeIds = new HashSet<TableIdentifier>(inScope.Count);
        foreach (var table in inScope)
        {
            inScopeIds.Add(table.Identifier);
        }

        var prefetchedProtections = await AddOwnContainerTablesAsync(tenantId, virtualFiltered, inScopeIds, activeTables, ct).ConfigureAwait(false);
        activeTables.Sort(static (a, b) => DiscoveryService.CompareTableKeys(a.Identifier, b.Identifier));

        var existingVersions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
        var ownersByTable = activeTables.Count > 0
            ? await _ownershipRepository.GetDataOwnersForTablesAsync(activeTables.Select(static t => t.Identifier).ToList(), ct).ConfigureAwait(false)
            : new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>();
        var artifactsByTable = _artifactStore is not null && activeTables.Count > 0
            ? await _artifactStore.GetAllForTablesAsync(activeTables.Select(static t => t.Table.Id).ToList(), ct).ConfigureAwait(false)
            : null;
        IReadOnlyDictionary<TableIdentifier, EffectiveProtection>? batchProtections = null;
        if (_protectionProvider is not null && activeTables.Count > 0)
        {
            var missing = prefetchedProtections is null
                ? activeTables
                : activeTables.Where(t => !prefetchedProtections.ContainsKey(t.Identifier)).ToList();
            if (missing.Count > 0)
            {
                batchProtections = await _protectionProvider.ResolveManyAsync(tenantId, missing, ct).ConfigureAwait(false);
            }
        }

        var upserts = new List<CatalogVersionUpsert>();
        var activeObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in activeTables)
        {
            var objectId = table.Identifier.ToString();
            activeObjectIds.Add(objectId);

            var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<ContainerOwnerEntry>();
            var artifacts = artifactsByTable is not null && artifactsByTable.TryGetValue(table.Table.Id, out var tableArtifacts)
                ? DiscoveryTenantScope.SelectPrimaryArtifact(tableArtifacts)
                : null;
            var protection = prefetchedProtections is not null && prefetchedProtections.TryGetValue(table.Identifier, out var preloaded)
                ? preloaded
                : batchProtections is not null && batchProtections.TryGetValue(table.Identifier, out var batchResolved)
                    ? batchResolved
                    : _protectionProvider is null
                        ? null
                        : await _protectionProvider.GetAsync(tenantId, table.Identifier, ct).ConfigureAwait(false);
            var hash = ExternalCatalogReader.ComputeAdminRepresentationHash(table, owners, artifacts, protection);

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

    private async Task<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>?> AddOwnContainerTablesAsync(
        TenantId tenantId,
        IReadOnlyList<TableMetadata> virtualFiltered,
        HashSet<TableIdentifier> inScopeIds,
        List<TableMetadata> activeTables,
        CancellationToken ct)
    {
        if (_protectionProvider is null || (_domainRepository is null && _productRepository is null))
        {
            return null;
        }

        var ownContainers = new HashSet<ContainerRef>();
        if (_domainRepository is not null)
        {
            var domains = await _domainRepository.ListAsync(tenantId, IGovernanceDomainRepository.MaxPageSize, 0, ct).ConfigureAwait(false);
            foreach (var domain in domains)
            {
                if (domain.Tenant == tenantId && !domain.IsSystemUnassigned)
                {
                    ownContainers.Add(new ContainerRef(ContainerKind.Domain, domain.Id));
                }
            }
        }

        if (_productRepository is not null)
        {
            var products = await _productRepository.ListProductsAsync(tenantId, null, IGovernanceDomainRepository.MaxPageSize, ct).ConfigureAwait(false);
            foreach (var product in products)
            {
                if (product.Tenant == tenantId)
                {
                    ownContainers.Add(new ContainerRef(ContainerKind.DataProduct, product.Id));
                }
            }
        }

        if (ownContainers.Count == 0)
        {
            return null;
        }

        var candidates = new List<TableMetadata>();
        foreach (var table in virtualFiltered)
        {
            if (table.Table.IsActive &&
                !inScopeIds.Contains(table.Identifier) &&
                (string.IsNullOrWhiteSpace(table.OwnerTenantId) || string.Equals(table.OwnerTenantId, tenantId.Value, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(table);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        IReadOnlyDictionary<TableIdentifier, EffectiveProtection> resolved;
        try
        {
            resolved = candidates.Count > 1
                ? await _protectionProvider.ResolveManyAsync(tenantId, candidates, ct).ConfigureAwait(false)
                : new Dictionary<TableIdentifier, EffectiveProtection>
                {
                    [candidates[0].Identifier] = await _protectionProvider.GetAsync(tenantId, candidates[0].Identifier, ct).ConfigureAwait(false)
                };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Catalog change indexer container resolution failed ({ErrorType}); skipping unconsented container additions.", ex.GetType().Name);
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (resolved.TryGetValue(candidate.Identifier, out var prot) && ownContainers.Contains(prot.Container))
            {
                activeTables.Add(candidate);
            }
        }

        return resolved;
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
