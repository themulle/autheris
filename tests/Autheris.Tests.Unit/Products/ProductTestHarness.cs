#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Products;
using Autheris.Application.State;
using Autheris.Domain.Model;
using NSubstitute;
using TrinoSqlEngine;

namespace Autheris.Tests.Unit.Products;

/// <summary>Tenant-scoped in-memory product store with the version semantics of the SQL store.</summary>
internal sealed class InMemoryDataProductRepository : IDataProductRepository, IOnDemandProductStore
{
    private readonly Dictionary<(string Tenant, string Schema, string Name), DataProduct> _products = [];
    private readonly Dictionary<(string Tenant, Guid Product), List<DataProductVersion>> _versions = [];

    public Task<DataProduct?> GetProductAsync(TenantId tenant, string schema, string name, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault((tenant.Value, schema, name)));

    public Task<DataProduct?> GetProductByIdAsync(TenantId tenant, Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.Values.FirstOrDefault(p => p.Tenant == tenant && p.Id == productId));

    public Task<IReadOnlyList<DataProduct>> ListProductsAsync(TenantId tenant, string? afterKey, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DataProduct>>(_products.Values.Where(p => p.Tenant == tenant)
            .OrderBy(p => p.Schema, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Where(p => afterKey is null || string.CompareOrdinal($"{p.Schema}/{p.Name}", afterKey) > 0).Take(limit).ToList());

    public Task<IReadOnlyList<DataProduct>> ListScheduledProductsAsync(DataProduct? after, int limit, CancellationToken ct = default)
    {
        var ordered = _products.Values.Where(p => p.Kind == DataProductKind.View && p.Load.Mode == LoadMode.Schedule && p.Status != DataProductStatus.Retired)
            .OrderBy(p => p.Tenant.Value, StringComparer.Ordinal).ThenBy(p => p.Schema, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal);
        var page = ordered.Where(p => after is null || string.CompareOrdinal($"{p.Tenant.Value}/{p.Schema}/{p.Name}", $"{after.Tenant.Value}/{after.Schema}/{after.Name}") > 0).Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<DataProduct>>(page);
    }

    public Task<IReadOnlyList<DataProduct>> ListOnDemandProductsAsync(DataProduct? after, int limit, CancellationToken ct = default)
    {
        var ordered = _products.Values.Where(p => p.Kind == DataProductKind.View && p.Load.Mode == LoadMode.OnDemand && p.Status != DataProductStatus.Retired)
            .OrderBy(p => p.Tenant.Value, StringComparer.Ordinal).ThenBy(p => p.Schema, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal);
        var page = ordered.Where(p => after is null || string.CompareOrdinal($"{p.Tenant.Value}/{p.Schema}/{p.Name}", $"{after.Tenant.Value}/{after.Schema}/{after.Name}") > 0).Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<DataProduct>>(page);
    }

    public Task<ProductWriteOutcome> CreateProductAsync(DataProduct product, DataProductVersion firstRevision, CancellationToken ct = default)
    {
        var key = (product.Tenant.Value, product.Schema, product.Name);
        if (!_products.TryAdd(key, product))
        {
            return Task.FromResult(new ProductWriteOutcome(ProductWriteStatus.AlreadyExists));
        }

        _versions[(product.Tenant.Value, product.Id)] = [firstRevision];
        return Task.FromResult(new ProductWriteOutcome(ProductWriteStatus.Created, product));
    }

    public Task<ProductWriteOutcome> ReplaceProductAsync(DataProduct product, int expectedVersion, DataProductVersion? definitionRevision, CancellationToken ct = default)
    {
        var key = (product.Tenant.Value, product.Schema, product.Name);
        if (!_products.TryGetValue(key, out var current))
        {
            return Task.FromResult(new ProductWriteOutcome(ProductWriteStatus.NotFound));
        }

        if (current.Version != expectedVersion)
        {
            return Task.FromResult(new ProductWriteOutcome(ProductWriteStatus.VersionMismatch));
        }

        var stored = product with { Version = expectedVersion + 1 };
        _products[key] = stored;
        if (definitionRevision is not null)
        {
            _versions[(product.Tenant.Value, product.Id)].Add(definitionRevision with { Version = stored.Version });
        }

        return Task.FromResult(new ProductWriteOutcome(ProductWriteStatus.Replaced, stored));
    }

    public Task<IReadOnlyList<DataProductVersion>> GetProductVersionsAsync(TenantId tenant, Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DataProductVersion>>(_versions.GetValueOrDefault((tenant.Value, productId)) ?? []);
}

internal sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
}

/// <summary>Lease double that is never lost.</summary>
internal sealed class StubLease : IDistributedLease
{
    public string ResourceKey => "stub";

    public long Token => 1;

    public CancellationToken LeaseLost => CancellationToken.None;

    public DateTimeOffset LastConfirmedAt => DateTimeOffset.UnixEpoch;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Disposable that does nothing.</summary>
internal sealed class NoopDisposable : IDisposable
{
    public void Dispose()
    {
    }
}

/// <summary>Product-only catalog writer double; applies the same identity guard as the SQL writer.</summary>
internal sealed class RecordingCatalogObjectWriter : IProductCatalogObjectWriter
{
    public List<TableMetadata> Written { get; } = [];

    public Task<TableMetadata> UpsertAsync(TenantId tenant, TableMetadata metadata, CancellationToken ct = default)
    {
        ProductSource.EnsureProductObject(tenant, metadata);
        Written.Add(metadata);
        return Task.FromResult(metadata);
    }
}

/// <summary>Records the owner links of product catalog objects.</summary>
internal sealed class RecordingOwnerStore : IProductCatalogOwnerStore
{
    public bool Fail { get; set; }

    public Dictionary<TableIdentifier, Sid?> Owners { get; } = [];

    public int Calls { get; private set; }

    public Task SetOwnerAsync(TenantId tenant, TableIdentifier catalogObject, Sid? owner, CancellationToken ct = default)
    {
        Calls++;
        if (Fail)
        {
            throw new InvalidOperationException("db down");
        }

        Owners[catalogObject] = owner;
        return Task.CompletedTask;
    }
}

/// <summary>Records the projected catalog object of every persisted product; <see cref="Fail"/> makes the next persists throw.</summary>
internal sealed class RecordingCatalogStore : IProductCatalogStore
{
    public Dictionary<TableIdentifier, TableMetadata> Objects { get; } = [];

    public bool Fail { get; set; }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UnixEpoch;

    public Task PersistAsync(DataProduct product, CancellationToken ct = default) => PersistAsync(product, null, ct);

    public Task PersistAsync(DataProduct product, DateTimeOffset? loadedAt, CancellationToken ct = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("catalog down");
        }

        var projected = ProductCatalogProjector.Project(product, loadedAt, Now());
        Objects[projected.Identifier] = projected;
        return Task.CompletedTask;
    }
}

/// <summary>Wires the real services over in-memory stores: owners per table, a stored catalog with PII flags and a recording audit/outbox.</summary>
internal sealed class ProductHarness
{
    public static readonly TenantId Tenant = new("tenant-a");
    public static readonly Sid Alice = new("S-1-5-21-1001");
    public static readonly Sid Bob = new("S-1-5-21-1002");
    public static readonly Sid Carol = new("S-1-5-21-1003");
    public static readonly Sid Admin = new("S-1-5-21-9001");
    public static readonly Sid SyncIdentity = new("S-1-5-21-9002");

    public static readonly TableIdentifier Customers = new("crm", "public", "customers");
    public static readonly TableIdentifier Orders = new("crm", "public", "orders");
    public static readonly TableIdentifier Products = new("erp", "public", "items");

    public InMemoryDataProductRepository Store { get; } = new();

    public RecordingAuditWriter Audit { get; } = new();

    public IConsentEventOutbox Outbox { get; } = Substitute.For<IConsentEventOutbox>();

    public Dictionary<string, HashSet<TableIdentifier>> Owned { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RecordingOwnerStore OwnerLinks { get; } = new();

    public RecordingCatalogStore CatalogObjects { get; } = new();

    public ProductAuthorityService Authority { get; }

    public DataProductService Service { get; }

    /// <summary>A cluster state provider that grants every lease.</summary>
    public static IDistributedClusterStateProvider LeasingCluster()
    {
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(new StubLease()));
        return cluster;
    }

    public ProductHarness()
    {
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.GetOwnedOrDelegatedTablesAsync(default, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var sid = call.ArgAt<Sid>(0).Value;
            var requested = call.ArgAt<IReadOnlyCollection<TableIdentifier>>(1);
            IReadOnlySet<TableIdentifier> owned = Owned.TryGetValue(sid, out var set) ? requested.Where(set.Contains).ToHashSet() : new HashSet<TableIdentifier>();
            return Task.FromResult(owned);
        });
        ownership.GetAuthorizedApproverSidsForTableAsync(default, default, default).ReturnsForAnyArgs(call =>
        {
            var table = call.ArgAt<TableIdentifier>(0);
            IReadOnlySet<string> sids = Owned.Where(o => o.Value.Contains(table)).Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(sids);
        });

        var catalog = Substitute.For<IProductCatalogColumns>();
        catalog.GetColumnsAsync(default, default, default).ReturnsForAnyArgs(call => Task.FromResult(Columns(call.ArgAt<TableIdentifier>(1))));

        Authority = new ProductAuthorityService(ownership, Store);
        Service = new DataProductService(Store, Authority, catalog, FastSqlEngine.Default, ownership, Audit, Outbox, ownershipSync: new ProductOwnershipSync(OwnerLinks), catalogStore: CatalogObjects);
    }

    public static IReadOnlyList<CatalogColumnInfo>? Columns(TableIdentifier table) => table.TableName switch
    {
        "customers" => [new("id", "bigint", false), new("email", "varchar", true), new("segment", "varchar", false)],
        "orders" => [new("id", "bigint", false), new("customer_id", "bigint", false), new("total", "decimal", false)],
        "items" => [new("sku", "varchar", false), new("price", "decimal", false)],
        _ => null
    };

    public ProductCaller Caller(Sid sid, bool admin = false, bool viaSync = false) => new(sid, Tenant, admin, viaSync, admin);

    public ProductPutRequest Put(ProductCaller caller, string sql, Sid? owner = null, string name = "orders_view", DataProductKind kind = DataProductKind.View) =>
        new(caller, "sales", name, kind, sql, owner);

    public const string CustomerOrders = "SELECT o.id, o.total, c.segment FROM crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id";
}
