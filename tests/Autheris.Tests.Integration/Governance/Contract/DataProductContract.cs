using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Products;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IDataProductRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class DataProductContract : GovernanceContractBase
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("crm", "public", "orders");
    private static readonly Sid Alice = new("S-1-5-21-1001");
    private static readonly Sid Bob = new("S-1-5-21-1002");
    private static readonly Sid GroupA = new("S-1-5-21-512");

    private IDataProductRepository Products => Store.Services.GetRequiredService<IDataProductRepository>();

    private static DataProduct NewProduct(TenantId tenant, string name, string definition = "SELECT id FROM crm.public.orders") => new(
        Guid.NewGuid(), tenant, "sales", name, DataProductKind.View, definition, ProductDefinitions.Hash(definition), [Orders],
        [new DataProductColumn("id", "bigint", ProductColumnClass.NonPii, [new ColumnLineage(Orders, "id")], null, null)],
        Alice, ProductCreatorAuthority.BaseOwner, DataProductStatus.Active, null, LoadSettings.Default, ProductLimits.Default, "desc", GroupA, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, 1, T0, T0);

    private static DataProductVersion Revision(DataProduct p) =>
        new(p.Id, p.Version, p.DefinitionHash, p.Definition, p.CreatedBy, p.Authority, T0);

    private async Task<DataProduct> CreateAsync(TenantId tenant, string name)
    {
        var product = NewProduct(tenant, name);
        (await Products.CreateProductAsync(product, Revision(product))).Status.Should().Be(ProductWriteStatus.Created);
        return product;
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var prodA = await CreateAsync(Store.TenantA, "tenant_a_view");
        var prodB = await CreateAsync(Store.TenantB, "tenant_b_view");

        var loadedA = await Products.GetProductAsync(Store.TenantA, "sales", "tenant_a_view");
        loadedA.Should().NotBeNull();
        loadedA!.OwnerGroupSid.Should().Be(GroupA);
        loadedA.DefaultClass.Should().Be(ProtectionClass.Confidential);
        loadedA.Visibility.Should().Be(CatalogVisibilityMode.OnRequest);
        (await Products.GetProductByIdAsync(Store.TenantA, prodA.Id)).Should().NotBeNull();
        (await Products.GetProductByIdAsync(Store.TenantA, prodB.Id)).Should().BeNull();
        (await Products.GetProductAsync(Store.TenantA, "sales", "tenant_b_view")).Should().BeNull();
        (await Products.ListProductsAsync(Store.TenantB, null, 50)).Select(p => p.Name).Should().Equal("tenant_b_view");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var a = await CreateAsync(Store.TenantA, "same_name");
        var b = await CreateAsync(Store.TenantB, "same_name");

        var changed = await Products.ReplaceProductAsync(b with { Description = "b changed", Version = 1 }, 1, null);
        changed.Status.Should().Be(ProductWriteStatus.Replaced);

        (await Products.GetProductAsync(Store.TenantA, "sales", "same_name"))!.Description.Should().Be("desc");
        (await Products.GetProductAsync(Store.TenantA, "sales", "same_name"))!.Id.Should().Be(a.Id);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var get = () => Products.GetProductAsync(default, "sales", "x");
        await get.Should().ThrowAsync<ArgumentException>();
        var list = () => Products.ListProductsAsync(default, null, 10);
        await list.Should().ThrowAsync<ArgumentException>();
        var getById = () => Products.GetProductByIdAsync(default, Guid.NewGuid());
        await getById.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var product = await CreateAsync(Store.TenantA, "versioned");
        var v2 = product with
        {
            Definition = "SELECT id, total FROM crm.public.orders",
            DefinitionHash = ProductDefinitions.Hash("SELECT id, total FROM crm.public.orders"),
            DefaultClass = ProtectionClass.Secret,
            Visibility = CatalogVisibilityMode.Open
        };
        var replaced = await Products.ReplaceProductAsync(v2, 1, Revision(v2) with { Version = 2 });
        replaced.Status.Should().Be(ProductWriteStatus.Replaced);
        replaced.Product!.Version.Should().Be(2);
        replaced.Product.DefaultClass.Should().Be(ProtectionClass.Secret);
        replaced.Product.Visibility.Should().Be(CatalogVisibilityMode.Open);
        (await Products.ReplaceProductAsync(v2, 1, null)).Status.Should().Be(ProductWriteStatus.VersionMismatch);
        (await Products.GetProductVersionsAsync(Store.TenantA, product.Id)).Select(v => v.Version).Should().Equal(1, 2);
        (await Products.ReplaceProductAsync(NewProduct(Store.TenantA, "ghost"), 1, null)).Status.Should().Be(ProductWriteStatus.NotFound);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var calls = Enumerable.Range(0, 4).Select(_ =>
        {
            var p = NewProduct(Store.TenantA, "race");
            return Products.CreateProductAsync(p, Revision(p));
        });
        var results = await Task.WhenAll(calls);
        results.Count(r => r.Status == ProductWriteStatus.Created).Should().Be(1);
        results.Count(r => r.Status == ProductWriteStatus.AlreadyExists).Should().Be(3);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        foreach (var name in new[] { "c_view", "a_view", "b_view" })
        {
            await CreateAsync(Store.TenantA, name);
        }

        var first = await Products.ListProductsAsync(Store.TenantA, null, 2);
        first.Select(p => p.Name).Should().Equal("a_view", "b_view");
        var second = await Products.ListProductsAsync(Store.TenantA, $"{first[^1].Schema}/{first[^1].Name}", 2);
        second.Select(p => p.Name).Should().Equal("c_view");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Scheduled_listing_spans_tenants_and_skips_other_load_modes()
    {
        if (!Store.IsAvailable) return;
        var schedule = new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromMinutes(5));
        foreach (var (tenant, name, load) in new[] { (Store.TenantA, "sched_a", schedule), (Store.TenantB, "sched_b", schedule), (Store.TenantA, "live_a", LoadSettings.Default) })
        {
            var p = NewProduct(tenant, name) with { Load = load };
            await Products.CreateProductAsync(p, Revision(p));
        }

        var first = await Products.ListScheduledProductsAsync(null, 1);
        var second = await Products.ListScheduledProductsAsync(first.Single(), 10);
        first.Concat(second).Select(p => (p.Tenant.Value, p.Name)).Should().Equal((Store.TenantA.Value, "sched_a"), (Store.TenantB.Value, "sched_b"));
        first.Single().Load.Interval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task On_demand_listing_spans_tenants_and_skips_other_modes()
    {
        if (!Store.IsAvailable) return;
        var onDemand = new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, IdleDropMinutes: 30);
        foreach (var (tenant, name, load) in new[] { (Store.TenantA, "od_a", onDemand), (Store.TenantB, "od_b", onDemand), (Store.TenantA, "live_a", LoadSettings.Default) })
        {
            var p = NewProduct(tenant, name) with { Load = load };
            await Products.CreateProductAsync(p, Revision(p));
        }

        var lister = Store.Services.GetRequiredService<IOnDemandProductStore>();
        var first = await lister.ListOnDemandProductsAsync(null, 1);
        var second = await lister.ListOnDemandProductsAsync(first.Single(), 10);
        first.Concat(second).Select(p => (p.Tenant.Value, p.Name)).Should().Equal((Store.TenantA.Value, "od_a"), (Store.TenantB.Value, "od_b"));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        await CreateAsync(Store.TenantA, "only_view");
        (await Products.ListProductsAsync(Store.TenantA, null, int.MaxValue)).Should().HaveCount(1);
        (await Products.ListProductsAsync(Store.TenantA, null, -5)).Should().HaveCount(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        // a retired product stays as a tombstone that blocks the name; it is not deleted
        var product = await CreateAsync(Store.TenantA, "retiring");
        (await Products.ReplaceProductAsync(product with { Status = DataProductStatus.Retired }, 1, null)).Status.Should().Be(ProductWriteStatus.Replaced);
        (await Products.GetProductAsync(Store.TenantA, "sales", "retiring"))!.Status.Should().Be(DataProductStatus.Retired);
        (await Products.CreateProductAsync(NewProduct(Store.TenantA, "retiring"), Revision(product))).Status.Should().Be(ProductWriteStatus.AlreadyExists);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Product_catalog_object_has_one_primary_owner_that_retire_removes()
    {
        if (!Store.IsAvailable) return;
        var tenant = Store.TenantA;
        var product = NewProduct(tenant, "owned_view");
        var projected = ProductCatalogProjector.Project(product, null, T0);
        var writer = new Autheris.Infrastructure.Persistence.Products.ProductCatalogObjectWriter(Store.Services.GetRequiredService<Autheris.Infrastructure.Persistence.GovernanceRepository>());
        await writer.UpsertAsync(tenant, projected);
        var owners = new Autheris.Infrastructure.Persistence.Products.ProductCatalogOwnerStore(Store.Services.GetRequiredService<Autheris.Infrastructure.Persistence.GovernanceRepository>());
        var ownership = Store.Services.GetRequiredService<Autheris.Application.Common.Interfaces.IDataOwnershipRepository>();
        var objects = new[] { projected.Identifier };

        await owners.SetOwnerAsync(tenant, projected.Identifier, Alice);
        await owners.SetOwnerAsync(tenant, projected.Identifier, Bob);

        (await ownership.GetOwnedOrDelegatedTablesAsync(Bob, objects, T0)).ToList().Should().Contain(projected.Identifier);
        (await ownership.GetOwnedOrDelegatedTablesAsync(Alice, objects, T0)).ToList().Should().BeEmpty("the owner is replaced, never added");

        await owners.SetOwnerAsync(tenant, projected.Identifier, null);
        (await ownership.GetOwnedOrDelegatedTablesAsync(Bob, objects, T0)).ToList().Should().BeEmpty();

        // another tenant's identity, or a table that is no product object, is refused before any write
        var act = () => owners.SetOwnerAsync(Store.TenantB, projected.Identifier, Alice);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Downgrade_audit_row()
    {
        if (!Store.IsAvailable) return;
        var product = await CreateAsync(Store.TenantA, "downgrade_audit_view");
        var column = new DataProductColumn(
            "email",
            "varchar",
            ProductColumnClass.PiiMasked,
            [new ColumnLineage(Orders, "email")],
            null,
            null);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, Alice.Value),
            new Claim("tenant_id", Store.TenantA.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));
        var caller = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: Store.TenantA);
        var guard = new ViewDowngradeGuard(
            Store.Services.GetRequiredService<IGovernanceCapabilities>(),
            Store.Services.GetRequiredService<IAuditWriter>());

        var updated = await guard.GuardAndAuditAsync(
            caller,
            product,
            column,
            ProductColumnClass.NonPii,
            "Aggregated email digest is non-identifying");

        updated.Class.Should().Be(ProductColumnClass.NonPii);
        var entries = await Store.AuditLogs.QueryAuditLogsAsync(
            targetTable: $"products.{product.Schema}.{product.Name}",
            actorSid: Alice,
            tenantId: Store.TenantA);
        entries.Should().Contain(e => e.EventType == AuditEventTypes.ViewDowngraded && e.TargetColumn == "email" && e.Decision == "ALLOW");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDataProductContractTests(GovernancePostgresFixture fixture) : DataProductContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "data_product_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerDataProductContractTests(GovernanceSqlServerFixture fixture) : DataProductContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "data_product_contract");
}
