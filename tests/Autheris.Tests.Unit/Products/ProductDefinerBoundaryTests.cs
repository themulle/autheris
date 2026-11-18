using Autheris.Application.Products;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Products.ProductDefinerTests;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

namespace Autheris.Tests.Unit.Products;

/// <summary>The definer reads exactly the base objects the creation authority approved, inside the product's tenant.</summary>
[Trait("Category", "Security")]
public sealed class ProductDefinerBoundaryTests
{
    [Fact]
    public async Task Definer_rejects_table_not_in_base_objects()
    {
        var pipeline = Pipeline();
        var approved = new[] { Customers.Identifier };

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            ((IProductDefinerExecutor)pipeline.Service).ExecuteAsync(
                new ProductDefiner(Product(Join, baseObjects: approved), ProductLimits.Default), Request(Join), default));
    }

    [Fact]
    public async Task Definer_runs_when_every_table_is_an_approved_base_object()
    {
        var pipeline = Pipeline();

        var result = await ((IProductDefinerExecutor)pipeline.Service).ExecuteAsync(
            new ProductDefiner(Product(Join), ProductLimits.Default), Request(Join), default);

        result.Rows.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Definer_rejects_foreign_tenant_base_table()
    {
        var foreign = Orders with { OwnerTenantId = "tenant-b" };
        var foreignPipeline = FederatedServiceHarness.Build(
            [
                (Customers, new StubTableConnector("crm", [Row(("id", "1"), ("name", "Alpha"))])),
                (foreign, new StubTableConnector("erp", [Row(("id", "1"), ("total", "10"))]))
            ],
            consents: NSubstitute.Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>());

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            ((IProductDefinerExecutor)foreignPipeline.Service).ExecuteAsync(
                new ProductDefiner(Product(Join), ProductLimits.Default), Request(Join), default));
    }

    [Fact]
    public async Task Definer_rejects_sql_different_from_definition()
    {
        var pipeline = Pipeline();
        var definer = new ProductDefiner(Product(Join), ProductLimits.Default);

        await Should.ThrowAsync<Exception>(() =>
            ((IProductDefinerExecutor)pipeline.Service).ExecuteAsync(definer, Request("SELECT c.id FROM crm.public.customers c"), default));
    }

    [Fact]
    public async Task ProductLoad_transport_without_definer_rejected()
    {
        var pipeline = Pipeline();

        await Should.ThrowAsync<Exception>(() => pipeline.Service.ExecuteQueryBufferedAsync(Request(Join), User(), Tenant));
    }

    [Fact]
    public async Task Definer_never_reads_product_object()
    {
        var productTable = SqlTable("products", "t_0123456789abcdef", "sales__other", "id", "name");
        var pipeline = Pipeline(extra: productTable);
        const string Sql = "SELECT p.id FROM products.t_0123456789abcdef.sales__other p JOIN erp.public.orders o ON p.id = o.id";

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            ((IProductDefinerExecutor)pipeline.Service).ExecuteAsync(
                new ProductDefiner(Product(Sql, baseObjects: [productTable.Identifier, Orders.Identifier]), ProductLimits.Default), Request(Sql), default));
    }
}
