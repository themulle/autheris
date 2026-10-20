namespace Autheris.Application.Catalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Tenant-scoped cached catalog lookup invalidated on <c>catalog:changed</c> events and bounded by a short TTL.
/// Fails closed (returns <c>null</c> on miss or unauthorized tenant) and never shares cache entries across tenants.
/// </summary>
public sealed class TenantCatalogLookup : ICatalogLookup, IDisposable
{
    private sealed record TenantCatalogSnapshot(
        DateTimeOffset ExpiresAt,
        IReadOnlyList<TableMetadata> Tables,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> ByIdentifier,
        IReadOnlyDictionary<string, TableMetadata> ByEntityName);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ITenantRegistry? _tenantRegistry;
    private readonly IConsentRepository? _consentRepository;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, TenantCatalogSnapshot> _tenantSnapshots = new(StringComparer.Ordinal);
    private readonly IDisposable? _eventSubscription;

    public TenantCatalogLookup(
        ITableMetadataRepository metadataRepository,
        IEventBus? eventBus = null,
        ITenantRegistry? tenantRegistry = null,
        IConsentRepository? consentRepository = null,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _tenantRegistry = tenantRegistry;
        _consentRepository = consentRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl is { Ticks: > 0 } validTtl ? validTtl : TimeSpan.FromSeconds(30);

        if (eventBus != null)
        {
            _eventSubscription = eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    Invalidate();
                    return Task.CompletedTask;
                });
        }
    }

    public async Task<TableMetadata?> FindByIdentifierAsync(TenantId tenantId, TableIdentifier identifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return null;
        }

        var snapshot = await GetOrLoadSnapshotAsync(tenantId, ct).ConfigureAwait(false);
        return snapshot.ByIdentifier.TryGetValue(identifier, out var metadata) ? metadata : null;
    }

    public async Task<TableMetadata?> FindByEntityNameAsync(TenantId tenantId, string entityName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId.Value) || string.IsNullOrWhiteSpace(entityName))
        {
            return null;
        }

        var snapshot = await GetOrLoadSnapshotAsync(tenantId, ct).ConfigureAwait(false);
        return snapshot.ByEntityName.TryGetValue(entityName.Trim(), out var metadata) ? metadata : null;
    }

    public async Task<IReadOnlyList<TableMetadata>> GetTenantTablesAsync(TenantId tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return [];
        }

        var snapshot = await GetOrLoadSnapshotAsync(tenantId, ct).ConfigureAwait(false);
        return snapshot.Tables;
    }

    public void Invalidate(TenantId? tenantId = null)
    {
        if (tenantId is { } specificTenant && !string.IsNullOrWhiteSpace(specificTenant.Value))
        {
            _tenantSnapshots.TryRemove(specificTenant.Value, out _);
            return;
        }

        _tenantSnapshots.Clear();
    }

    private async Task<TenantCatalogSnapshot> GetOrLoadSnapshotAsync(TenantId tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();

        if (_tenantSnapshots.TryGetValue(tenantId.Value, out var cached) && cached.ExpiresAt > now)
        {
            return cached;
        }

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        HashSet<TableIdentifier>? consentedTables = null;
        if (_consentRepository != null)
        {
            var tenantConsents = await _consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct).ConfigureAwait(false);
            if (tenantConsents.Count > 0)
            {
                consentedTables = tenantConsents.ToHashSet();
            }
        }

        var visibleTables = new List<TableMetadata>(allTables.Count);
        var byIdentifier = new Dictionary<TableIdentifier, TableMetadata>();
        var byEntityName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in allTables)
        {
            if (!table.Table.IsActive)
            {
                continue;
            }

            if (!IsTableVisibleToTenant(table, tenantId, consentedTables))
            {
                continue;
            }

            visibleTables.Add(table);
            byIdentifier[table.Identifier] = table;

            string domainTable = $"{table.Identifier.Domain}_{table.Identifier.TableName}";
            string domainSchemaTable = $"{table.Identifier.Domain}_{table.Identifier.Schema}_{table.Identifier.TableName}";
            string qualified = table.Identifier.ToString();

            byEntityName.TryAdd(domainSchemaTable, table);
            byEntityName.TryAdd(domainTable, table);
            byEntityName.TryAdd(qualified, table);
            byEntityName.TryAdd(table.Identifier.TableName, table);
        }

        var built = new TenantCatalogSnapshot(now.Add(_ttl), visibleTables, byIdentifier, byEntityName);
        _tenantSnapshots[tenantId.Value] = built;
        return built;
    }

    private bool IsTableVisibleToTenant(
        TableMetadata table,
        TenantId tenantId,
        HashSet<TableIdentifier>? consentedTables)
    {
        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            if (string.Equals(table.OwnerTenantId, tenantId.Value, StringComparison.Ordinal))
            {
                return true;
            }

            return consentedTables != null && consentedTables.Contains(table.Identifier);
        }

        if (consentedTables != null)
        {
            return consentedTables.Contains(table.Identifier);
        }

        return true;
    }

    public void Dispose()
    {
        _eventSubscription?.Dispose();
    }
}
