using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Products;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Audit;
using Autheris.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>The product grant answer of a test: the listed sids may read, with every column in clear.</summary>
internal sealed class GrantedReaders(params Sid[] readers) : IProductReadAccess
{
    public Task<ProductAccessView> ViewAsync(SecurityPrincipalContext caller, DataProduct product, CancellationToken ct = default) =>
        Task.FromResult(readers.Contains(caller.UserSid)
            ? new ProductAccessView(true, product.Columns.ToDictionary(c => c.Name, c => c.Class == ProductColumnClass.PiiMasked ? "masked" : "clear"))
            : new ProductAccessView(false, new Dictionary<string, string>()));
}

internal sealed class InactiveBaseObjects : IProductBaseObjectCheck
{
    public Task<bool> AllActiveAsync(IReadOnlyList<TableIdentifier> baseObjects, CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class FixedRefreshTimes(DateTimeOffset? at) : IProductRefreshTimes
{
    public DateTimeOffset? LoadedAt(DataProduct product) => at;
}

/// <summary>Discovery, explain, read audit, lifecycle audit and usage of data products: caller scoped, never a base object, SQL or value.</summary>
[Trait("Category", "Security")]
public sealed class ProductDiscoveryTests
{
    private static readonly DateTimeOffset Loaded = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly ProductHarness _h = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    private static SecurityPrincipalContext Context(Sid sid) => new()
    {
        UserSid = sid,
        TenantId = ProductHarness.Tenant,
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Test"
    };

    private ProductDiscoveryService Discovery(params Sid[] readers) => new(_h.Store, new GrantedReaders(readers), new FixedRefreshTimes(Loaded), _time);

    private async Task<DataProduct> CreateAsync(string name = "orders_view")
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Customers, ProductHarness.Orders];
        return await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders, name: name));
    }

    [Fact]
    public async Task Discovery_hides_unreadable_products()
    {
        var product = await CreateAsync();
        var discovery = Discovery(ProductHarness.Bob);

        var bob = await discovery.ListAsync(Context(ProductHarness.Bob), _h.Caller(ProductHarness.Bob), null, 50);
        var carol = await discovery.ListAsync(Context(ProductHarness.Carol), _h.Caller(ProductHarness.Carol), null, 50);
        var owner = await discovery.ListAsync(Context(ProductHarness.Alice), _h.Caller(ProductHarness.Alice), null, 50);

        var item = bob.Items.ShouldHaveSingleItem();
        item.Name.ShouldBe("orders_view");
        item.Kind.ShouldBe(nameof(DataProductKind.View));
        item.ProductOwner.ShouldBe(ProductHarness.Alice.Value);
        item.RefreshedAt.ShouldBe(Loaded);
        item.Columns.Select(c => c.Name).ShouldBe(product.Columns.Select(c => c.Name));
        item.Columns.ShouldAllBe(c => c.State == "masked" || c.State == "clear");
        carol.Items.ShouldBeEmpty("a caller without a product grant sees nothing");
        owner.Items.ShouldHaveSingleItem("the product owner administers the product");

        // a retired product leaves discovery for readers, an administrator still sees it
        await _h.Service.RetireAsync(_h.Caller(ProductHarness.Alice), "sales", "orders_view");
        (await discovery.ListAsync(Context(ProductHarness.Bob), _h.Caller(ProductHarness.Bob), null, 50)).Items.ShouldBeEmpty();
        (await discovery.ListAsync(Context(ProductHarness.Admin), _h.Caller(ProductHarness.Admin, admin: true), null, 50)).Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Explain_hides_sql_and_lineage_from_readers()
    {
        await CreateAsync();
        var discovery = Discovery(ProductHarness.Bob);

        var reader = await discovery.ExplainAsync(Context(ProductHarness.Bob), _h.Caller(ProductHarness.Bob), "sales", "orders_view");
        var owner = await discovery.ExplainAsync(Context(ProductHarness.Alice), _h.Caller(ProductHarness.Alice), "sales", "orders_view");

        reader.Allowed.ShouldBeTrue();
        reader.BaseRulesApply.ShouldBeFalse("the product grant decides, base rules do not apply");
        reader.DecidedBy.ShouldBe("product-grant");
        reader.BaseObjects.ShouldBeNull();
        reader.DefinitionHash.ShouldBeNull();
        reader.Lineage.ShouldBeNull();
        var json = JsonSerializer.Serialize(reader);
        json.ShouldNotContain("crm", Case.Insensitive);
        json.ShouldNotContain("SELECT", Case.Insensitive);
        json.ShouldNotContain("customers", Case.Insensitive);

        owner.BaseObjects.ShouldNotBeNull().ShouldNotBeEmpty();
        owner.DefinitionHash.ShouldNotBeNullOrEmpty();
        owner.Lineage.ShouldNotBeNull();
        JsonSerializer.Serialize(owner).ShouldNotContain("SELECT", Case.Insensitive);
    }

    [Fact]
    public async Task Unknown_and_hidden_product_explain_identically()
    {
        await CreateAsync();
        var discovery = Discovery(ProductHarness.Bob);

        var hidden = await Should.ThrowAsync<ProductException>(() => discovery.ExplainAsync(Context(ProductHarness.Carol), _h.Caller(ProductHarness.Carol), "sales", "orders_view"));
        var unknown = await Should.ThrowAsync<ProductException>(() => discovery.ExplainAsync(Context(ProductHarness.Carol), _h.Caller(ProductHarness.Carol), "sales", "nope"));
        var usageHidden = await Should.ThrowAsync<ProductException>(() => discovery.UsageAsync(Context(ProductHarness.Carol), _h.Caller(ProductHarness.Carol), "sales", "orders_view"));

        (hidden.Failure, hidden.Code).ShouldBe((unknown.Failure, unknown.Code));
        (usageHidden.Failure, usageHidden.Code).ShouldBe((unknown.Failure, unknown.Code));
        unknown.Code.ShouldBe(ProductErrorCodes.NotFound);
    }

    [Fact]
    public async Task Foreign_tenant_product_is_not_listed_or_explained()
    {
        await CreateAsync();
        var foreign = new ProductCaller(ProductHarness.Alice, new TenantId("tenant-b"), false);

        (await Discovery(ProductHarness.Alice).ListAsync(Context(ProductHarness.Alice), foreign, null, 50)).Items.ShouldBeEmpty();
        await Should.ThrowAsync<ProductException>(() => Discovery(ProductHarness.Alice).ExplainAsync(Context(ProductHarness.Alice), foreign, "sales", "orders_view"));
    }

    [Fact]
    public async Task Product_read_audit_carries_id_and_hash()
    {
        var product = await CreateAsync();
        var usage = new ProductUsageTracker();
        var audit = new ProductReadAudit(_h.Store, new FixedRefreshTimes(Loaded), _h.Audit, usage, _time);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, ProductHarness.Bob.Value)], "test"));
        _h.Audit.Entries.Clear();

        await audit.RecordReadAsync(principal, ProductHarness.Tenant, "sales", "orders_view", 42);

        var entry = _h.Audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.ProductRead);
        entry.ActorSid.ShouldBe(ProductHarness.Bob);
        using var details = JsonDocument.Parse(entry.DetailsJson);
        details.RootElement.GetProperty("productId").GetGuid().ShouldBe(product.Id);
        details.RootElement.GetProperty("definitionHash").GetString().ShouldBe(product.DefinitionHash);
        details.RootElement.GetProperty("loadMode").GetString().ShouldBe(product.Load.Mode.ToString());
        details.RootElement.GetProperty("resultAgeSeconds").GetInt64().ShouldBe(3600);
        details.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(42);
        usage.Snapshot(ProductHarness.Tenant, product.Id).RowsRead.ShouldBe(42);

        // a product that is gone fails the read instead of leaving it unaudited
        await Should.ThrowAsync<ProductLoadException>(() => audit.RecordReadAsync(principal, ProductHarness.Tenant, "sales", "missing", 1));
    }

    [Fact]
    public async Task Reproject_and_idle_drop_audited()
    {
        var onDemand = new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, IdleDropMinutes: 30);
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Customers, ProductHarness.Orders];
        var product = await _h.Service.PutAsync(new ProductPutRequest(
            _h.Caller(ProductHarness.Admin, admin: true), "sales", "orders_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, onDemand));
        var cluster = ProductHarness.LeasingCluster();
        var runner = new ScriptedRunner { Failure = _ => new InvalidOperationException("source down") };
        var catalogStore = new RecordingCatalogStore();
        var loader = new ProductLoadService(_h.Store, runner, new RecordingCacheStore(), new TableCacheCatalog(), cluster, _h.Audit, _time, null, catalogStore);
        _h.Audit.Entries.Clear();

        // a failed first load turns the product invalid, which re-projects (deactivates) its catalog object
        await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(product));
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductReprojected);
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductLoadFailed);
        var reprojected = _h.Audit.Entries.Single(e => e.EventType == AuditEventTypes.ProductReprojected);
        reprojected.DetailsJson.ShouldContain(product.Id.ToString());
        reprojected.DetailsJson.ShouldContain(product.DefinitionHash);

        // an idle drop of a loaded result is on record
        runner.Failure = _ => null;
        var current = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        await loader.GetResultAsync(current);
        _time.Advance(TimeSpan.FromMinutes(31));
        (await loader.DropIdleAsync([current])).ShouldBe(1);
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductIdleDropped && e.DetailsJson.Contains(product.DefinitionHash));
    }

    [Fact]
    public async Task Load_with_inactive_base_object_marks_product_invalid()
    {
        var product = await CreateAsync();
        var cluster = ProductHarness.LeasingCluster();
        var runner = new ScriptedRunner();
        var check = new InactiveBaseObjects();
        var loader = new ProductLoadService(_h.Store, runner, new RecordingCacheStore(), new TableCacheCatalog(), cluster, _h.Audit, _time, null, null, check);

        var ex = await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(product));

        ex.Code.ShouldBe(ProductErrorCodes.BaseObjectInactive);
        runner.Calls.ShouldBe(0, "no source is queried for a product whose base object is no longer active");
        (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!.Status.ShouldBe(DataProductStatus.Invalid);
    }

    [Fact]
    public async Task Usage_cardinality_bounded()
    {
        var usage = new ProductUsageTracker();
        var tenant = ProductHarness.Tenant;
        var known = Guid.NewGuid();
        usage.RecordRead(tenant, known, 1);

        for (var i = 0; i < ProductUsageTracker.MaxTrackedProducts + 25; i++)
        {
            usage.RecordRead(tenant, Guid.NewGuid(), 1);
        }

        usage.TrackedProducts.ShouldBe(ProductUsageTracker.MaxTrackedProducts);
        usage.Overflow.ShouldBe(26);

        // a tracked product keeps counting, a blank tenant is never tracked, and the snapshot holds no caller information
        usage.RecordLoad(tenant, known, true, 5, 7);
        usage.Snapshot(tenant, known).Loads.ShouldBe(1);
        typeof(ProductUsageSnapshot).GetProperties().Select(p => p.Name).ShouldNotContain(n => n.Contains("Sid") || n.Contains("Caller") || n.Contains("User"));
    }

    [Fact]
    public async Task Product_over_product_stays_rejected()
    {
        await CreateAsync();
        var inner = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view").ToQualifiedName();

        var ex = await Should.ThrowAsync<ProductException>(() => _h.Service.PutAsync(
            _h.Put(_h.Caller(ProductHarness.Alice), $"SELECT id FROM {inner}", name: "outer_view")));

        ex.Failure.ShouldBeOneOf(ProductFailure.Invalid, ProductFailure.Forbidden);
    }
}
