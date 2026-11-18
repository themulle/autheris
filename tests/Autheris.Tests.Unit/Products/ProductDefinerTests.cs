using Autheris.Application.Products;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options.TableCache;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

namespace Autheris.Tests.Unit.Products;

/// <summary>
/// Definer mode of stage 1: the definition runs as the product through the real governed federation pipeline. Only the consent decision of
/// the base objects is skipped; everything else of the pipeline still applies.
/// </summary>
[Trait("Category", "Security")]
public sealed class ProductDefinerTests
{
    internal static readonly TableMetadata Customers = SqlTable("crm", "public", "customers", "id", "name");
    internal static readonly TableMetadata Orders = SqlTable("erp", "public", "orders", "id", "total");

    internal const string Join = "SELECT c.id, c.name, o.total FROM crm.public.customers c JOIN erp.public.orders o ON c.id = o.id";

    internal static FederatedServiceHarness.Pipeline Pipeline(int? maxStagedRowsPerTable = null, params TableMetadata[] extra) =>
        FederatedServiceHarness.Build(
            [
                (Customers, new StubTableConnector("crm", [Row(("id", "1"), ("name", "Alpha")), Row(("id", "2"), ("name", "Beta")), Row(("id", "3"), ("name", "Gamma"))])),
                (Orders, new StubTableConnector("erp", [Row(("id", "1"), ("total", "10")), Row(("id", "2"), ("total", "20")), Row(("id", "3"), ("total", "30"))])),
                .. extra.Select(t => (t, new StubTableConnector(t.Identifier.Domain, [Row(("id", "1"), ("name", "x"))])))
            ],
            consents: Substitute.For<Autheris.Application.Common.Interfaces.IConsentRepository>(),
            maxStagedRowsPerTable: maxStagedRowsPerTable);

    internal static DataProduct Product(string sql, TenantId? tenant = null, IReadOnlyList<TableIdentifier>? baseObjects = null) => new(
        Guid.NewGuid(), tenant ?? Tenant, "sales", "orders_view", DataProductKind.View, sql, ProductDefinitions.Hash(sql),
        baseObjects ?? [Customers.Identifier, Orders.Identifier], [],
        new Sid("S-1-5-21-1"), ProductCreatorAuthority.DataProductAdmin, DataProductStatus.Active, null,
        new LoadSettings(LoadMode.OnDemand, TtlMinutes: 5), ProductLimits.Default, null, new Sid("S-1-5-21-2"));

    internal static GovernedSqlQueryRequest Request(string sql, string transport = SqlTransports.ProductLoad) => new(sql, Transport: transport);

    [Fact]
    public async Task Definer_identity_skips_only_base_consent_keeps_admission_and_limits()
    {
        var pipeline = Pipeline();
        var executor = (IProductDefinerExecutor)pipeline.Service;

        // the same statement is denied for a caller without grants ...
        await Should.ThrowAsync<WebSqlTableDeniedException>(() => pipeline.RunAsync(Join));

        // ... and runs for the definer without any grant on the base objects
        var result = await executor.ExecuteAsync(new ProductDefiner(Product(Join), ProductLimits.Default), Request(Join), default);
        result.Rows.Select(r => $"{r["id"]}|{r["total"]}").OrderBy(x => x, StringComparer.Ordinal).ToArray().ShouldBe(["1|10", "2|20", "3|30"]);
        pipeline.Audit.ReceivedCalls().ShouldNotBeEmpty("the definer run is audited like any federated query");

        // read-only enforcement: a definition that writes is refused
        const string Dml = "DELETE FROM crm.public.customers WHERE id = 1";
        await Should.ThrowAsync<Exception>(() => executor.ExecuteAsync(new ProductDefiner(Product(Dml), ProductLimits.Default), Request(Dml), default));

        // the staging row limit still applies
        var limited = Pipeline(maxStagedRowsPerTable: 2);
        await Should.ThrowAsync<Exception>(() => ((IProductDefinerExecutor)limited.Service).ExecuteAsync(new ProductDefiner(Product(Join), ProductLimits.Default), Request(Join), default));

        // unknown tables are refused like for any caller (admission)
        const string Unknown = "SELECT c.id FROM crm.public.secrets c JOIN erp.public.orders o ON c.id = o.id";
        await Should.ThrowAsync<Exception>(() => executor.ExecuteAsync(new ProductDefiner(Product(Unknown), ProductLimits.Default), Request(Unknown), default));
    }

    [Fact]
    public async Task Definer_runs_only_its_own_definition_in_its_own_tenant_and_transport()
    {
        var pipeline = Pipeline();
        var executor = (IProductDefinerExecutor)pipeline.Service;
        var definer = new ProductDefiner(Product(Join), ProductLimits.Default);

        // another statement than the definition, or another transport, is refused
        await Should.ThrowAsync<Exception>(() => executor.ExecuteAsync(definer, Request("SELECT c.id FROM crm.public.customers c"), default));
        await Should.ThrowAsync<Exception>(() => executor.ExecuteAsync(definer, Request(Join, SqlTransports.WebSql), default));

        // the product load transport is never usable without a definer (client path)
        await Should.ThrowAsync<Exception>(() => pipeline.Service.ExecuteQueryBufferedAsync(Request(Join), User(), Tenant));
    }

    [Fact]
    public async Task A_product_definition_never_reads_a_product_object()
    {
        var productTable = SqlTable("products", "t_0123456789abcdef", "sales__other", "id", "name");
        var pipeline = Pipeline(extra: productTable);
        const string Sql = "SELECT p.id FROM products.t_0123456789abcdef.sales__other p JOIN erp.public.orders o ON p.id = o.id";

        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            ((IProductDefinerExecutor)pipeline.Service).ExecuteAsync(new ProductDefiner(Product(Sql), ProductLimits.Default), Request(Sql), default));
    }

    [Fact]
    public async Task Load_end_to_end_with_definer_runner_produces_cached_result()
    {
        var pipeline = Pipeline();
        var services = new ServiceCollection();
        services.AddScoped<IProductDefinerExecutor>(_ => (IProductDefinerExecutor)pipeline.Service);
        var runner = new FederationProductQueryRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var harness = new ProductHarness();
        var admin = harness.Caller(ProductHarness.Admin, admin: true);
        harness.Owned[ProductHarness.Admin.Value] = [];
        var created = await harness.Service.PutAsync(new ProductPutRequest(admin, "sales", "orders_view", DataProductKind.View, Join, ProductHarness.Bob));
        var store = new RecordingCacheStore();
        var catalog = new TableCacheCatalog();
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(Substitute.For<IDistributedLease>()));
        var service = new ProductLoadService(harness.Store, runner, store, catalog, cluster, harness.Audit,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero)));

        // the product tenant is the tenant of the pipeline: re-store under it
        var product = created with { Tenant = ProductHarness.Tenant };
        var result = await service.LoadAsync(product);

        result.CacheKey.ShouldBe(ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view"));
        store.Staged.ShouldHaveSingleItem().Rows.ShouldBe(3);
        catalog.TryGet(result.CacheKey, out var meta).ShouldBeTrue();
        meta!.RowCount.ShouldBe(3);
    }

    [Fact]
    public async Task Runner_fails_closed_without_the_federation_engine()
    {
        var runner = new FederationProductQueryRunner(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        var ex = await Should.ThrowAsync<ProductLoadException>(() => runner.RunAsync(new ProductDefiner(Product(Join), ProductLimits.Default), default));

        ex.Code.ShouldBe(ProductErrorCodes.LoadFailed);
    }
}
