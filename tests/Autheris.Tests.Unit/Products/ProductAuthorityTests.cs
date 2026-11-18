using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductAuthorityTests
{
    private readonly ProductHarness _h = new();

    private static readonly TableIdentifier[] CustomerOrderObjects = [ProductHarness.Orders, ProductHarness.Customers];

    [Fact]
    public async Task Owner_creates_over_own_objects_and_becomes_product_owner()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders, ProductHarness.Customers];

        var product = await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders));

        product.ProductOwner.ShouldBe(ProductHarness.Alice);
        product.Authority.ShouldBe(ProductCreatorAuthority.BaseOwner);
        product.CreatedBy.ShouldBe(ProductHarness.Alice);
        product.BaseObjects.ShouldBe(CustomerOrderObjects, ignoreOrder: true);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Delegate_creates_over_owners_objects()
    {
        // the ownership repository reports owned-or-delegated tables for the delegate's SID
        _h.Owned[ProductHarness.Bob.Value] = [ProductHarness.Orders, ProductHarness.Customers];

        var decision = await _h.Authority.AuthorizeCreationAsync(_h.Caller(ProductHarness.Bob), DataProductKind.View, CustomerOrderObjects, null);

        decision.Allowed.ShouldBeTrue();
        decision.Authority.ShouldBe(ProductCreatorAuthority.BaseOwner);
        decision.ProductOwner.ShouldBe(ProductHarness.Bob);
    }

    [Fact]
    public async Task Owner_cannot_include_foreign_object()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders];

        var ex = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders)));

        ex.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
        ex.Failure.ShouldBe(ProductFailure.Forbidden);
        (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view")).ShouldBeNull();
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductAccessDenied && e.Decision == "DENY");
    }

    [Fact]
    public async Task DataSourceOwner_creates_views_and_procedures_in_own_source()
    {
        await _h.Store.AddDataSourceOwnerAsync(new DataSourceOwner(ProductHarness.Tenant, "crm", ProductHarness.Carol, ProductHarness.Admin, DateTimeOffset.UnixEpoch));
        var carol = _h.Caller(ProductHarness.Carol);

        var view = await _h.Service.PutAsync(_h.Put(carol, ProductHarness.CustomerOrders));
        var procedure = await _h.Service.PutAsync(_h.Put(carol, "crm.public.refresh_customers", name: "refresh", kind: DataProductKind.Procedure));

        view.Authority.ShouldBe(ProductCreatorAuthority.DataSourceOwner);
        view.ProductOwner.ShouldBe(ProductHarness.Carol);
        procedure.Kind.ShouldBe(DataProductKind.Procedure);
        procedure.ProductOwner.ShouldBe(ProductHarness.Carol);

        var foreign = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(carol, "SELECT sku FROM erp.public.items", name: "items_view")));
        foreign.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
    }

    [Fact]
    public async Task Only_datasource_owner_admin_or_sync_create_procedures()
    {
        var procedure = new[] { new TableIdentifier("crm", "public", "refresh_customers") };
        _h.Owned[ProductHarness.Alice.Value] = [procedure[0]];
        await _h.Store.AddDataSourceOwnerAsync(new DataSourceOwner(ProductHarness.Tenant, "crm", ProductHarness.Carol, ProductHarness.Admin, DateTimeOffset.UnixEpoch));

        (await _h.Authority.AuthorizeCreationAsync(_h.Caller(ProductHarness.Alice), DataProductKind.Procedure, procedure, null))
            .ErrorCode.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
        (await _h.Authority.AuthorizeCreationAsync(_h.Caller(ProductHarness.Carol), DataProductKind.Procedure, procedure, null))
            .Authority.ShouldBe(ProductCreatorAuthority.DataSourceOwner);
        (await _h.Authority.AuthorizeCreationAsync(_h.Caller(ProductHarness.Admin, admin: true), DataProductKind.Procedure, procedure, ProductHarness.Bob))
            .Authority.ShouldBe(ProductCreatorAuthority.DataProductAdmin);
        (await _h.Authority.AuthorizeCreationAsync(_h.Caller(ProductHarness.SyncIdentity, viaSync: true), DataProductKind.Procedure, procedure, ProductHarness.Bob))
            .Authority.ShouldBe(ProductCreatorAuthority.GitSync);
    }

    [Fact]
    public async Task Admin_must_designate_other_product_owner()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);

        (await _h.Authority.AuthorizeCreationAsync(admin, DataProductKind.View, CustomerOrderObjects, null)).ErrorCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);
        (await _h.Authority.AuthorizeCreationAsync(admin, DataProductKind.View, CustomerOrderObjects, ProductHarness.Admin)).ErrorCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);
        (await _h.Authority.AuthorizeCreationAsync(admin, DataProductKind.View, CustomerOrderObjects, new Sid(" "))).ErrorCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);

        var created = await _h.Service.PutAsync(_h.Put(admin, ProductHarness.CustomerOrders, ProductHarness.Bob));
        created.ProductOwner.ShouldBe(ProductHarness.Bob);
        created.Authority.ShouldBe(ProductCreatorAuthority.DataProductAdmin);
        created.CreatedBy.ShouldBe(ProductHarness.Admin);

        var selfOwned = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(admin, ProductHarness.CustomerOrders, ProductHarness.Admin, name: "other_view")));
        selfOwned.Code.ShouldBe(ProductErrorCodes.OwnerMustDiffer);
    }

    [Fact]
    public async Task Sync_identity_never_product_owner()
    {
        var sync = _h.Caller(ProductHarness.SyncIdentity, viaSync: true);

        (await _h.Authority.AuthorizeCreationAsync(sync, DataProductKind.View, CustomerOrderObjects, ProductHarness.SyncIdentity)).ErrorCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);
        (await _h.Authority.AuthorizeCreationAsync(sync, DataProductKind.View, CustomerOrderObjects, null)).ErrorCode.ShouldBe(ProductErrorCodes.OwnerMustDiffer);

        var product = await _h.Service.PutAsync(_h.Put(sync, ProductHarness.CustomerOrders, ProductHarness.Alice));
        product.ProductOwner.ShouldBe(ProductHarness.Alice);
        product.Authority.ShouldBe(ProductCreatorAuthority.GitSync);
        product.ManagedBy.ShouldBe("Git");
        ProductAuthorityService.IsProductOwner(sync, product).ShouldBeFalse();
    }

    [Fact]
    public async Task Transfer_only_by_admin_never_to_self()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders, ProductHarness.Customers];
        await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders));
        var admin = _h.Caller(ProductHarness.Admin, admin: true);

        var byOwner = await Should.ThrowAsync<ProductException>(() => _h.Service.TransferAsync(_h.Caller(ProductHarness.Alice), "sales", "orders_view", ProductHarness.Bob));
        byOwner.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
        var toSelf = await Should.ThrowAsync<ProductException>(() => _h.Service.TransferAsync(admin, "sales", "orders_view", ProductHarness.Admin));
        toSelf.Code.ShouldBe(ProductErrorCodes.OwnerMustDiffer);

        var moved = await _h.Service.TransferAsync(admin, "sales", "orders_view", ProductHarness.Bob);
        moved.ProductOwner.ShouldBe(ProductHarness.Bob);
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductOwnerTransferred && e.ActorSid == ProductHarness.Admin);
    }

    [Fact]
    public async Task Product_owner_cannot_change_definition_or_classification()
    {
        // Bob owns the product (transferred by an admin) but holds no authority over the base objects.
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        await _h.Service.PutAsync(_h.Put(admin, ProductHarness.CustomerOrders, ProductHarness.Bob));
        var bob = _h.Caller(ProductHarness.Bob);

        var definition = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(bob, "SELECT id, total FROM crm.public.orders")));
        definition.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
        var classification = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.ClassifyColumnAsync(bob, "sales", "orders_view", "segment", ProductColumnClass.PiiClearEligible, "needed"));
        classification.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);

        // the product owner still manages description and load mode
        var updated = await _h.Service.UpdateSettingsAsync(bob, "sales", "orders_view", "Sales by segment", new LoadSettings(LoadMode.OnDemand, 30));
        updated.Description.ShouldBe("Sales by segment");
        updated.Load.Mode.ShouldBe(LoadMode.OnDemand);
        var stored = await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view");
        stored!.Definition.ShouldBe(ProductHarness.CustomerOrders);

        var notOwner = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.UpdateSettingsAsync(_h.Caller(ProductHarness.Carol), "sales", "orders_view", "x", null));
        notOwner.Code.ShouldBe(ProductErrorCodes.OwnerOnlyOperation);
    }

    [Fact]
    public async Task Definition_change_needs_authority_over_old_and_new_objects_and_keeps_the_owner()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders, ProductHarness.Customers];
        _h.Owned[ProductHarness.Bob.Value] = [ProductHarness.Orders];
        await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders));

        // Bob owns only orders: he cannot take over a product that also reads customers by narrowing the definition
        var takeover = await Should.ThrowAsync<ProductException>(() =>
            _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Bob), "SELECT id, total FROM crm.public.orders")));
        takeover.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);

        var changed = await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), "SELECT id, total FROM crm.public.orders"));
        changed.ProductOwner.ShouldBe(ProductHarness.Alice);
        changed.Version.ShouldBe(2);
        (await _h.Store.GetProductVersionsAsync(ProductHarness.Tenant, changed.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Products_are_tenant_scoped()
    {
        _h.Owned[ProductHarness.Alice.Value] = [ProductHarness.Orders, ProductHarness.Customers];
        await _h.Service.PutAsync(_h.Put(_h.Caller(ProductHarness.Alice), ProductHarness.CustomerOrders));

        var otherTenant = new ProductCaller(ProductHarness.Alice, new TenantId("tenant-b"), false, false, true);
        var ex = await Should.ThrowAsync<ProductException>(() => _h.Service.GetAsync(otherTenant, "sales", "orders_view"));

        ex.Failure.ShouldBe(ProductFailure.NotFound);
        (await _h.Service.ListAsync(otherTenant, null, 50)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_catalog_objects_and_non_catalog_ids_are_invalid_definitions()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);

        foreach (var sql in new[] { "SELECT * FROM crm.public.unknown_table", "SELECT id FROM orders", "SELECT 1", "DELETE FROM crm.public.orders" })
        {
            var ex = await Should.ThrowAsync<ProductException>(() => _h.Service.PutAsync(_h.Put(admin, sql, ProductHarness.Bob)));
            ex.Code.ShouldBe(ProductErrorCodes.DefinitionInvalid);
        }
    }
}
