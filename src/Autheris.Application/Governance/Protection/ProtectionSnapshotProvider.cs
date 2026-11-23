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
    /// Snapshots for a batch of decisions over <paramref name="tables"/> (the metadata those decisions use): a valid cache entry is returned
    /// as <see cref="GetAsync"/> would; every other active table is resolved exactly as <see cref="LoadSnapshotAsync"/> resolves it, but from
    /// the given metadata and with set-based container and protection reads instead of a metadata, a container and a protection read per
    /// table. Those snapshots are NOT cached: they were built from caller-supplied metadata, so they serve only this batch (the caller bounds
    /// their age). Inactive tables, data product tables and tables the batch reads did not answer are left out, so the decision resolves them
    /// per table. Errors propagate (the caller then resolves per table, where a failure is fail-closed).
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>> ResolveManyAsync(
        TenantId tenant, IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var tenantKey = RequireValidTenant(tenant);
        var result = new Dictionary<TableIdentifier, EffectiveProtection>(tables.Count);
        if (tables.Count == 0)
        {
            return result;
        }

        var catalogVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        var governanceEpoch = await _epochProvider.GetEpochAsync(tenant, ct).ConfigureAwait(false);

        var resolvable = new Dictionary<TableIdentifier, TableMetadata>();
        foreach (var meta in tables)
        {
            RequireValidTable(meta.Identifier);
            if (result.ContainsKey(meta.Identifier) || resolvable.ContainsKey(meta.Identifier))
            {
                continue;
            }

            if (_cache.TryGet(new CacheKey(tenantKey, meta.Identifier, catalogVersion, governanceEpoch), out var cached) && !IsExpired(cached))
            {
                result[meta.Identifier] = cached.Protection;
            }
            else if (meta.Table.IsActive &&
                     meta.Table.Id != Guid.Empty &&
                     (_productRepository is null ||
                      !string.Equals(meta.Table.SourceName, ProductSource.Name, StringComparison.OrdinalIgnoreCase)))
            {
                resolvable[meta.Identifier] = meta;
            }
        }

        if (resolvable.Count == 0)
        {
            return result;
        }

        var ids = resolvable.Values.Select(static m => m.Table.Id).Distinct().ToList();
        var containers = await _domainRepository.GetContainersForTablesAsync(tenant, ids, ct).ConfigureAwait(false);
        var protections = await _domainRepository.GetProtectionBatchAsync(tenant, ids, ct).ConfigureAwait(false);
        foreach (var (table, meta) in resolvable)
        {
            if (containers.TryGetValue(meta.Table.Id, out var container) &&
                protections.TryGetValue(meta.Table.Id, out var protection))
            {
                var columnNames = meta.Columns.Select(static c => c.ColumnName).ToList();
                result[table] = EffectiveProtectionResolver.Resolve(container, protection.Table, protection.Columns, columnNames);
            }
        }

        return result;
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
