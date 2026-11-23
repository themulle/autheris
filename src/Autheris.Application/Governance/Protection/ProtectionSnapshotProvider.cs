using System.Collections.Concurrent;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Products;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Protection;

/// <summary>
/// Resolves and caches <see cref="EffectiveProtection"/> snapshots keyed by
/// <c>(tenant, table, catalogVersion, governanceEpoch)</c> with bounded positive/negative TTLs and fail-closed error propagation.
/// </summary>
public sealed class ProtectionSnapshotProvider : IProtectionSnapshotProvider, IGovernanceCacheInvalidator, IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly record struct CacheKey(
        string TenantId,
        TableIdentifier Table,
        CatalogVersion CatalogVersion,
        long GovernanceEpoch);

    private sealed record CachedSnapshot(EffectiveProtection Protection, bool IsNegative, long CachedAtTimestamp);

    private readonly ITableMetadataRepository _tableMetadataRepository;
    private readonly IGovernanceDomainRepository _domainRepository;
    private readonly GovernanceEpochProvider _epochProvider;
    private readonly ICatalogVersionProvider _catalogVersionProvider;
    private readonly IDataProductRepository? _productRepository;
    private readonly TimeSpan _maxTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly TimeProvider _time;
    private readonly BoundedCache<CacheKey, CachedSnapshot> _cache;
    private readonly ConcurrentDictionary<string, long> _tenantGenerations = new(StringComparer.Ordinal);
    private readonly IDisposable? _changeSubscription;
    private readonly IDisposable? _epochSubscription;
    private long _globalGeneration;
    private int _disposed;

    public ProtectionSnapshotProvider(
        ITableMetadataRepository tableMetadataRepository,
        IGovernanceDomainRepository domainRepository,
        GovernanceEpochProvider epochProvider,
        ICatalogVersionProvider catalogVersionProvider,
        IOptions<GovernanceCacheSettings>? cacheSettings = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null,
        IDataProductRepository? productRepository = null)
    {
        _tableMetadataRepository = tableMetadataRepository ?? throw new ArgumentNullException(nameof(tableMetadataRepository));
        _domainRepository = domainRepository ?? throw new ArgumentNullException(nameof(domainRepository));
        _epochProvider = epochProvider ?? throw new ArgumentNullException(nameof(epochProvider));
        _catalogVersionProvider = catalogVersionProvider ?? throw new ArgumentNullException(nameof(catalogVersionProvider));
        _productRepository = productRepository;

        var settings = cacheSettings?.Value ?? new GovernanceCacheSettings();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxTtlSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxTtlSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.NegativeTtlSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.NegativeTtlSeconds, 60);

        _maxTtl = TimeSpan.FromSeconds(settings.MaxTtlSeconds);
        _negativeTtl = TimeSpan.FromSeconds(Math.Min(settings.NegativeTtlSeconds, settings.MaxTtlSeconds));
        _time = timeProvider ?? TimeProvider.System;
        _cache = new BoundedCache<CacheKey, CachedSnapshot>(DefaultCapacity, _maxTtl, _time);

        _changeSubscription = changeNotifier?.Subscribe(OnChange);
        _epochSubscription = _epochProvider.OnTenantEpochAdvanced(tenantId =>
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                InvalidateAll();
            }
            else
            {
                InvalidateTenant(tenantId);
            }
        });
    }

    public async ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
    {
        var tenantKey = RequireValidTenant(tenant);
        RequireValidTable(table);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var startGlobalGen = Volatile.Read(ref _globalGeneration);
            var startTenantGen = _tenantGenerations.GetValueOrDefault(tenantKey);

            var catalogVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            var governanceEpoch = await _epochProvider.GetEpochAsync(tenant, ct).ConfigureAwait(false);
            var key = new CacheKey(tenantKey, table, catalogVersion, governanceEpoch);

            if (_cache.TryGet(key, out var cached))
            {
                if (!IsExpired(cached) && IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
                {
                    return cached.Protection;
                }

                _cache.TryRemove(key);
            }

            var entry = await GetOrAddSnapshotAsync(key, tenant, table, ct).ConfigureAwait(false);

            if (!IsSameGeneration(tenantKey, startGlobalGen, startTenantGen) || IsExpired(entry))
            {
                _cache.TryRemove(key);
                if (attempt == 0)
                {
                    continue;
                }
            }

            return entry.Protection;
        }

        throw new InvalidOperationException("Protection snapshot resolution could not stabilize.");
    }

    /// <summary>
    /// Loads the uncached snapshots of <paramref name="tables"/> with one metadata batch read and set-based container and protection reads
    /// instead of a metadata, a container and a protection read per table. Each snapshot is resolved exactly as <see cref="LoadSnapshotAsync"/>
    /// resolves it and is cached under the key <see cref="GetAsync"/> uses, only when no invalidation happened during the load; data product
    /// tables keep the per-table path. Errors propagate (the caller then decides per table, where a failure is fail-closed).
    /// </summary>
    public async ValueTask PrefetchAsync(TenantId tenant, IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var tenantKey = RequireValidTenant(tenant);
        if (tables.Count == 0)
        {
            return;
        }

        var startGlobalGen = Volatile.Read(ref _globalGeneration);
        var startTenantGen = _tenantGenerations.GetValueOrDefault(tenantKey);
        var catalogVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        var governanceEpoch = await _epochProvider.GetEpochAsync(tenant, ct).ConfigureAwait(false);

        var missing = new List<TableIdentifier>();
        foreach (var table in tables.Distinct())
        {
            RequireValidTable(table);
            if (!_cache.TryGet(new CacheKey(tenantKey, table, catalogVersion, governanceEpoch), out var cached) || IsExpired(cached))
            {
                missing.Add(table);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        var metadata = await _tableMetadataRepository.GetTableMetadataBatchAsync(missing, ct).ConfigureAwait(false);
        var resolvable = new List<(TableIdentifier Table, TableMetadata Metadata)>(missing.Count);
        var snapshots = new List<(TableIdentifier Table, CachedSnapshot Snapshot)>(missing.Count);
        for (var i = 0; i < missing.Count; i++)
        {
            var meta = i < metadata.Count ? metadata[i] : null;
            if (meta is null || !meta.Table.IsActive)
            {
                var negative = EffectiveProtectionResolver.Resolve(ContainerDefaults.Unassigned, null, null, null);
                snapshots.Add((missing[i], new CachedSnapshot(negative, IsNegative: true, _time.GetTimestamp())));
            }
            else if (_productRepository is null ||
                     !string.Equals(meta.Table.SourceName, ProductSource.Name, StringComparison.OrdinalIgnoreCase))
            {
                resolvable.Add((missing[i], meta));
            }
        }

        if (resolvable.Count > 0)
        {
            var ids = resolvable.Select(static r => r.Metadata.Table.Id).ToList();
            var containers = await _domainRepository.GetContainersForTablesAsync(tenant, ids, ct).ConfigureAwait(false);
            var protections = await _domainRepository.GetProtectionBatchAsync(tenant, ids, ct).ConfigureAwait(false);
            foreach (var (table, meta) in resolvable)
            {
                var tableId = meta.Table.Id;
                if (!containers.TryGetValue(tableId, out var container) || !protections.TryGetValue(tableId, out var protection))
                {
                    continue; // Not answered by the batch read: GetAsync loads it per table.
                }

                var columnNames = meta.Columns.Select(static c => c.ColumnName).ToList();
                var resolved = EffectiveProtectionResolver.Resolve(container, protection.Table, protection.Columns, columnNames);
                var isNegative = !container.IsValid
                    || (container.Container.Id == Guid.Empty && protection.Table is null && protection.Columns.Count == 0);
                snapshots.Add((table, new CachedSnapshot(resolved, isNegative, _time.GetTimestamp())));
            }
        }

        // An invalidation during the load may have made the read rows stale: cache nothing, GetAsync reads again.
        if (!IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
        {
            return;
        }

        foreach (var (table, snapshot) in snapshots)
        {
            _cache.Set(new CacheKey(tenantKey, table, catalogVersion, governanceEpoch), snapshot);
        }
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            InvalidateAll();
            return;
        }

        _tenantGenerations.AddOrUpdate(tenantId, 1, static (_, g) => g + 1);
        foreach (var key in _cache.Keys)
        {
            if (string.Equals(key.TenantId, tenantId, StringComparison.Ordinal))
            {
                _cache.TryRemove(key);
            }
        }
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _globalGeneration);
        _cache.Clear();
    }

    private Task<CachedSnapshot> GetOrAddSnapshotAsync(CacheKey key, TenantId tenant, TableIdentifier table, CancellationToken ct) =>
        _cache.GetOrAddAsync(key, (_, innerCt) => LoadSnapshotAsync(tenant, table, innerCt), ct);

    private async Task<CachedSnapshot> LoadSnapshotAsync(TenantId tenant, TableIdentifier table, CancellationToken ct)
    {
        var tableMetadata = await _tableMetadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (tableMetadata is null || !tableMetadata.Table.IsActive)
        {
            var missing = EffectiveProtectionResolver.Resolve(ContainerDefaults.Unassigned, null, null, null);
            return new CachedSnapshot(missing, IsNegative: true, _time.GetTimestamp());
        }

        var tableId = tableMetadata.Table.Id;
        ContainerDefaults container;
        if (_productRepository is not null &&
            string.Equals(tableMetadata.Table.SourceName, ProductSource.Name, StringComparison.OrdinalIgnoreCase))
        {
            var product = await _productRepository.GetProductByIdAsync(tenant, tableId, ct).ConfigureAwait(false);
            container = product is not null
                ? new ContainerDefaults(
                    new ContainerRef(ContainerKind.DataProduct, product.Id),
                    product.OwnerGroupSid,
                    product.DefaultClass,
                    product.Visibility,
                    IsValid: true)
                : await _domainRepository.GetContainerForTableAsync(tenant, tableId, ct).ConfigureAwait(false);
        }
        else
        {
            container = await _domainRepository.GetContainerForTableAsync(tenant, tableId, ct).ConfigureAwait(false);
        }
        var (tableProtection, columnProtections) = await _domainRepository.GetProtectionAsync(tenant, tableId, ct).ConfigureAwait(false);
        var columnNames = tableMetadata.Columns.Select(static c => c.ColumnName).ToList();

        var resolved = EffectiveProtectionResolver.Resolve(container, tableProtection, columnProtections, columnNames);
        var isNegative = !container.IsValid
            || (container.Container.Id == Guid.Empty && tableProtection is null && columnProtections.Count == 0);

        return new CachedSnapshot(resolved, isNegative, _time.GetTimestamp());
    }

    private bool IsExpired(CachedSnapshot entry) =>
        _time.GetElapsedTime(entry.CachedAtTimestamp) >= (entry.IsNegative ? _negativeTtl : _maxTtl);

    private bool IsSameGeneration(string tenantKey, long startGlobalGen, long startTenantGen) =>
        Volatile.Read(ref _globalGeneration) == startGlobalGen
        && _tenantGenerations.GetValueOrDefault(tenantKey) == startTenantGen;

    private void OnChange(GovernanceChangeEvent change)
    {
        if (change.IsFullInvalidation)
        {
            InvalidateAll();
        }
        else
        {
            InvalidateTenant(change.TenantId);
        }
    }

    private static string RequireValidTenant(TenantId tenant) =>
        !string.IsNullOrWhiteSpace(tenant.Value) && TenantId.TryParse(tenant.Value, out _)
            ? tenant.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));

    private static void RequireValidTable(TableIdentifier table)
    {
        if (string.IsNullOrWhiteSpace(table.Domain)
            || string.IsNullOrWhiteSpace(table.Schema)
            || string.IsNullOrWhiteSpace(table.TableName))
        {
            throw new ArgumentException("A valid TableIdentifier is required.", nameof(table));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changeSubscription?.Dispose();
        _epochSubscription?.Dispose();
    }
}
