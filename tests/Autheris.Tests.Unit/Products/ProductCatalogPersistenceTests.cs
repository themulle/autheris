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

    private async Task<DataProduct> CreateAsync(string sql = "SELECT c.id, c.email, c.segment FROM crm.public.customers c")
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        return await _h.Service.PutAsync(_h.Put(admin, sql, ProductHarness.Bob));
    }

    [Fact]
    public async Task Catalog_object_persisted_with_classified_columns()
    {
        var product = await CreateAsync();
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.UpsertTableMetadataAsync(default!, default).ReturnsForAnyArgs(call => Task.FromResult(call.Arg<TableMetadata>()));
        var classifications = Substitute.For<IClassificationRepository>();
        var store = new ProductCatalogStore(metadata, classifications);

        await store.PersistAsync(product);

        var written = (TableMetadata)metadata.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ITableMetadataRepository.UpsertTableMetadataAsync)).GetArguments()[0]!;
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

        await _h.Service.TransferAsync(_h.Caller(ProductHarness.Admin, admin: true), product.Schema, product.Name, ProductHarness.Carol);

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
