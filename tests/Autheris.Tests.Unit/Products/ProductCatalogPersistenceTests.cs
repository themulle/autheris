using Autheris.Application.Common.Interfaces;
using Autheris.Application.Products;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductCatalogPersistenceTests
{
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view");

    private readonly ProductHarness _h = new();
    private readonly IClassificationRepository _classifications = Substitute.For<IClassificationRepository>();

    [Fact]
    public async Task ProductCatalogStore_PersistsValidProductIdentity()
    {
        var product = await CreateAsync();
        var writer = new RecordingCatalogObjectWriter();

        await new ProductCatalogStore(writer, _classifications).PersistAsync(product);

        var written = writer.Written.Single();
        written.Identifier.ShouldBe(ProductObject);
        written.Identifier.Domain.ShouldBe(ProductSource.Name);
        Should.NotThrow(() => ProductSource.EnsureProductObject(ProductHarness.Tenant, written));
    }

    [Fact]
    public async Task CatalogImport_ProductsSource_StillRejected()
    {
        var product = await CreateAsync();
        var projected = ProductCatalogProjector.Project(product, null, DateTimeOffset.UnixEpoch);

        Should.Throw<Autheris.Application.Catalog.Services.CatalogReservedSourceNameException>(
            () => Autheris.Application.Catalog.Services.CatalogSourceNameValidator.Validate(projected));
    }

    [Fact]
    public async Task ProductCatalogPath_RejectsForeignTenantHash()
    {
        var product = await CreateAsync();
        var projected = ProductCatalogProjector.Project(product, null, DateTimeOffset.UnixEpoch);
        var writer = new RecordingCatalogObjectWriter();
        var foreign = new TenantId("tenant-b");
        var regular = projected with
        {
            Identifier = new TableIdentifier("crm", projected.Identifier.Schema, projected.Identifier.TableName),
        };

        await Should.ThrowAsync<ArgumentException>(() => writer.UpsertAsync(foreign, projected));
        await Should.ThrowAsync<ArgumentException>(() => writer.UpsertAsync(ProductHarness.Tenant, regular));
        writer.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProductCatalogWriter_ForeignOwnerTenant_IsRejected()
    {
        var product = await CreateAsync();
        var projected = ProductCatalogProjector.Project(product, null, DateTimeOffset.UnixEpoch);
        var writer = new RecordingCatalogObjectWriter();

        await Should.ThrowAsync<ArgumentException>(() => writer.UpsertAsync(ProductHarness.Tenant, projected with { OwnerTenantId = "tenant-b" }));
        await Should.ThrowAsync<ArgumentException>(() => writer.UpsertAsync(ProductHarness.Tenant, projected with { OwnerTenantId = null }));
        writer.Written.ShouldBeEmpty();
    }

    [Fact]
    public void ProductCatalogWriter_ReservedName_IsRejected()
    {
        var reserved = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "pg_orders");
        var metadata = new TableMetadata
        {
            Identifier = reserved,
            OwnerTenantId = ProductHarness.Tenant.Value,
            Table = new Table { SourceName = reserved.Domain, SchemaName = reserved.Schema, TableName = reserved.TableName, IsActive = true }
        };

        Should.Throw<ArgumentException>(() => ProductSource.EnsureProductObject(ProductHarness.Tenant, metadata));
    }

    private async Task<DataProduct> CreateAsync(string sql = "SELECT c.id, c.email, c.segment FROM crm.public.customers c")
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        return await _h.Service.PutAsync(_h.Put(admin, sql, ProductHarness.Bob));
    }

    [Fact]
    public async Task Catalog_object_persisted_with_classified_columns()
    {
        var product = await CreateAsync();
        var writer = new RecordingCatalogObjectWriter();
        var classifications = _classifications;
        var store = new ProductCatalogStore(writer, classifications);

        await store.PersistAsync(product);

        var written = writer.Written.Single();
        written.Identifier.ShouldBe(ProductObject);
        written.Table.IsActive.ShouldBeTrue();
        var email = written.Columns.Single(c => c.ColumnName == "email");
        var segment = written.Columns.Single(c => c.ColumnName == "segment");
        await classifications.Received(1).AssignColumnClassificationAsync(
            Arg.Is<ColumnClassification>(a => a.TableColumnId == email.Id.ToString() && a.Code == ClassificationScopes.PiiCode && a.Scope == ClassificationScopes.Global), Arg.Any<CancellationToken>());
        await classifications.DidNotReceive().AssignColumnClassificationAsync(
            Arg.Is<ColumnClassification>(a => a.TableColumnId == segment.Id.ToString()), Arg.Any<CancellationToken>());
        await classifications.Received(1).RemoveColumnClassificationAsync(ClassificationScopes.Global, segment.Id.ToString(), ClassificationScopes.PiiCode, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Service_persists_the_catalog_object_on_create_and_owner_transfer()
    {
        var product = await CreateAsync();
        _h.CatalogObjects.Objects[ProductObject].Table.IsActive.ShouldBeTrue();
        _h.OwnerLinks.Owners[ProductObject].ShouldBe(ProductHarness.Bob);

        await _h.Service.SetOwnerGroupAsync(_h.Caller(ProductHarness.Admin, admin: true), product.Schema, product.Name, ProductHarness.Carol);

        _h.OwnerLinks.Owners[ProductObject].ShouldBe(ProductHarness.Carol);
        _h.CatalogObjects.Objects[ProductObject].Table.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Retire_deactivates_catalog_object_and_removes_owner()
    {
        await CreateAsync();

        await _h.Service.RetireAsync(_h.Caller(ProductHarness.Bob), "sales", "orders_view");

        _h.CatalogObjects.Objects[ProductObject].Table.IsActive.ShouldBeFalse("a retired product is no longer decidable");
        _h.OwnerLinks.Owners[ProductObject].ShouldBeNull();
    }

    [Fact]
    public async Task Failed_catalog_persist_fails_the_product_operation()
    {
        _h.CatalogObjects.Fail = true;

        var ex = await Should.ThrowAsync<ProductException>(() => CreateAsync());

        ex.Code.ShouldBe(ProductErrorCodes.CatalogPersistFailed);
        _h.OwnerLinks.Calls.ShouldBe(0, "the owner link follows the catalog object, never precedes it");
    }
}
