using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductSyncTests
{
    private const string ViewFile =
        "schema: sales\nname: orders_view\nkind: view\nproduct_owner: S-1-5-21-1001\nlimits:\n  max_rows: 1000\ndefinition: |\n  SELECT o.id, o.total, c.segment FROM crm.public.orders o JOIN crm.public.customers c ON o.customer_id = c.id\n";

    private readonly ProductHarness _h = new();
    private readonly ProductSyncService _sync;

    public ProductSyncTests() => _sync = new ProductSyncService(_h.Service, _h.Store, _h.Audit);

    private ProductSyncRequest Request(params ProductSyncFile[] files) =>
        new(_h.Caller(ProductHarness.SyncIdentity, viaSync: true), "abcdef0123", files);

    [Fact]
    public async Task Sync_requires_sync_role_and_signed_webhook()
    {
        var rest = new ProductSyncRequest(_h.Caller(ProductHarness.Alice), "abcdef0123", [new ProductSyncFile("p.product.yaml", ViewFile)]);

        (await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(rest))).Reason.ShouldBe(ProductSyncReasons.NotAllowed);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull();
    }

    [Fact]
    public async Task Git_managed_definition_read_only_in_api()
    {
        await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)));

        var ex = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Admin, admin: true), "SELECT id FROM crm.public.orders", ProductHarness.Alice)));

        ex.Code.ShouldBe(ProductErrorCodes.ManagedByGit);
    }

    [Fact]
    public async Task Drift_reported_not_overwritten()
    {
        // a product created through the API (not Git-managed) with the same name is drift: reported, never overwritten
        var manual = await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Admin, admin: true), "SELECT id FROM crm.public.orders", ProductHarness.Bob));

        var result = await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)));

        result.Items.ShouldHaveSingleItem().Action.ShouldBe(ProductSyncAction.Drift);
        var stored = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;
        stored.DefinitionHash.ShouldBe(manual.DefinitionHash);
        stored.ProductOwner.ShouldBe(ProductHarness.Bob);
        stored.ManagedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Changed_owner_in_file_is_drift_and_unchanged_commit_is_a_noop()
    {
        await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)));
        var before = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;

        (await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)))).Items.Single().Action.ShouldBe(ProductSyncAction.Unchanged);
        (await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile.Replace("1001", "1002"))))).Items.Single().Action.ShouldBe(ProductSyncAction.Drift);

        var after = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;
        after.Version.ShouldBe(before.Version);
        after.ProductOwner.ShouldBe(ProductHarness.Alice);
    }

    [Fact]
    public async Task Changed_definition_creates_a_new_revision()
    {
        await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)));

        var result = await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile.Replace("o.total, ", string.Empty))));

        result.Items.Single().Action.ShouldBe(ProductSyncAction.Change);
        var product = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;
        product.Columns.Select(c => c.Name).ShouldBe(["id", "segment"]);
        product.Limits.MaxRows.ShouldBe(1000);
    }

    [Fact]
    public async Task Product_owner_functions_remain_on_git_products()
    {
        await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile)));

        var updated = await _h.Service.UpdateSettingsAsync(_h.Caller(ProductHarness.Alice), "sales", "orders_view", "owner text", null);

        updated.Description.ShouldBe("owner text");
        updated.ManagedBy.ShouldBe("Git");
        // a later sync does not overwrite the owner's description
        await _sync.ApplyAsync(Request(new ProductSyncFile("p.product.yaml", ViewFile.Replace("o.total, ", string.Empty))));
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!.Description.ShouldBe("owner text");
    }

    [Fact]
    public async Task Retiring_many_products_in_one_commit_is_refused()
    {
        for (var i = 0; i <= ProductSyncService.MaxRetirementsPerSync; i++)
        {
            await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.SyncIdentity, viaSync: true), "SELECT id FROM crm.public.orders", ProductHarness.Alice, name: $"p{i}"));
        }

        (await Should.ThrowAsync<ProductSyncRejectedException>(() => _sync.ApplyAsync(Request()))).Reason.ShouldBe(ProductSyncReasons.TooManyRetirements);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "p0"))!.Status.ShouldBe(DataProductStatus.Active);
    }
}
