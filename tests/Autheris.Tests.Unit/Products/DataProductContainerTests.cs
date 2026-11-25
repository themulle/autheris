#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Products;
using Autheris.Application.Security;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Autheris.Tests.Common.Fakes;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class DataProductContainerTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid DomainOwner = new("S-1-5-21-100-200-300-10");
    private static readonly Sid DomainOwnerGroup = new("S-1-5-21-100-200-300-512");
    private static readonly Sid OtherGroup = new("S-1-5-21-100-200-300-513");
    private static readonly Sid AdminSid = new("S-1-5-21-100-200-300-99");
    private static readonly TableIdentifier OrdersTable = new("crm", "public", "orders");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static SecurityPrincipalContext Context(Sid sid, string role, params Sid[] groups)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: TenantA) with
        {
            GroupSids = new HashSet<Sid>(groups),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    private sealed class StubPolicyRepo : IAccessPolicySettingsRepository
    {
        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<StoredTenantAccessPolicy?>(new StoredTenantAccessPolicy(
                TenantA, new TenantAccessPolicyUpdate(DefaultContainerVisibility: CatalogVisibilityMode.Open), 1, AdminSid, T0));
        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteIfVersionAsync(TenantId tenant, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubDomainRepo(Guid domainId, Guid tableId) : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GovernanceDomain>>([]);
        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult<GovernanceDomain?>(id == domainId
                ? new GovernanceDomain(domainId, TenantA, "Sales", null, null, DomainOwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0)
                : null);
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid id, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid id, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid id, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid id, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid id, CancellationToken ct = default) =>
            Task.FromResult(id == tableId
                ? new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), DomainOwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsValid: true)
                : ContainerDefaults.Unassigned);
    }

    private sealed class StubCatalogColumns : IProductCatalogColumns
    {
        public Task<IReadOnlyList<CatalogColumnInfo>?> GetColumnsAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogColumnInfo>?>([new("id", "bigint", false), new("total", "decimal", false)]);
    }

    private sealed class CaptureDefinerExecutor : IProductDefinerExecutor
    {
        public ProductDefiner? Captured { get; private set; }
        public Task<GovernedSqlResult> ExecuteAsync(ProductDefiner definer, GovernedSqlQueryRequest request, CancellationToken ct)
        {
            Captured = definer;
            return Task.FromResult(new GovernedSqlResult(
                "SELECT id FROM crm.public.orders", ["id"],
                [new Dictionary<string, object?> { ["id"] = 42L }], 1, 1, Truncated: false));
        }
    }

    [Fact]
    public void ProductColumnClass_maps_to_expected_mask_category()
    {
        ProductColumnAccess.ToMaskCategory(ProductColumnClass.PiiMasked).ShouldBe(ClassificationScopes.PiiCode);
        ProductColumnAccess.ToMaskCategory(ProductColumnClass.PiiClearEligible).ShouldBe(ClassificationScopes.PiiCode);
        ProductColumnAccess.ToMaskCategory(ProductColumnClass.NonPii).ShouldBeNull();
    }

    [Fact]
    public async Task Container_owner_group_creation_and_visibility_default_from_policy()
    {
        var store = new InMemoryDataProductRepository();
        var domainRepo = Substitute.For<IGovernanceDomainRepository>();
        var domainId = Guid.NewGuid();
        var ordersGuid = Guid.NewGuid();
        var tableRepo = new InMemoryTableMetadataRepository(new TableMetadata { Identifier = OrdersTable, Table = new Table { Id = ordersGuid, IsActive = true } });
        var ownershipRepo = new InMemoryContainerOwnership();
        var audit = new RecordingAuditWriter();
        domainRepo.GetContainerForTableAsync(TenantA, ordersGuid, Arg.Any<CancellationToken>())
            .Returns(new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), DomainOwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsValid: true));
        domainRepo.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Sales", null, null, DomainOwnerGroup, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0));

        var policyProvider = new AccessPolicySettingsProvider(new StubPolicyRepo(), Options.Create(new AccessPolicyOptions { DefaultContainerVisibility = CatalogVisibilityMode.Open }), audit, new FakeTimeProvider(T0));

        var caps = new GovernanceCapabilities(domainRepository: domainRepo, productRepository: store);
        var authority = new ProductAuthorityService(ownershipRepo, store, new FakeTimeProvider(T0), caps, domainRepo, tableRepo);
        var cols = new StubCatalogColumns();
        var service = new DataProductService(
            store, authority, cols, FastSqlEngine.Default, ownershipRepo, audit,
            timeProvider: new FakeTimeProvider(T0),
            downgradeGuard: new ViewDowngradeGuard(caps, audit, new FakeTimeProvider(T0)),
            policyProvider: policyProvider);

        var ownerCtx = Context(DomainOwner, "Consumer", DomainOwnerGroup);
        var caller = ProductAuthorityService.ForRestRoute(DomainOwner, TenantA, false, false, false, ownerCtx)!;

        var created = await service.PutAsync(new ProductPutRequest(
            caller, "sales", "orders_container", DataProductKind.View,
            "SELECT id, total FROM crm.public.orders",
            DefaultClass: ProtectionClass.Internal));

        created.OwnerGroupSid.ShouldBe(DomainOwnerGroup);
        created.DefaultClass.ShouldBe(ProtectionClass.Internal);
        created.Visibility.ShouldBe(CatalogVisibilityMode.Open);
        (await caps.IsOwnerMemberAsync(ownerCtx, new ContainerRef(ContainerKind.DataProduct, created.Id), CancellationToken.None)).ShouldBeTrue();

        var adminCtx = Context(AdminSid, "DataProductAdmin");
        var adminCaller = ProductAuthorityService.ForRestRoute(AdminSid, TenantA, true, false, true, adminCtx)!;
        var reassigned = await service.SetOwnerGroupAsync(adminCaller, "sales", "orders_container", OtherGroup);
        reassigned.OwnerGroupSid.ShouldBe(OtherGroup);
        (await caps.IsOwnerMemberAsync(ownerCtx, new ContainerRef(ContainerKind.DataProduct, created.Id), CancellationToken.None)).ShouldBeFalse();
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.OwnerGroupAssigned);
    }

    [Fact]
    public async Task Definer_semantics_unchanged()
    {
        var store = new InMemoryDataProductRepository();
        var cacheStore = Substitute.For<ITableCacheStore>();
        cacheStore.StageShadowGenerationAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IAsyncEnumerable<IReadOnlyList<object?>>>(), Arg.Any<CancellationToken>())
            .Returns("shadow-1");
        cacheStore.SwapActiveGenerationAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(call => new TableCacheEntryMetadata(
                call.ArgAt<TableIdentifier>(0), TableCacheStorageTier.Memory, "mem", call.ArgAt<string>(2), call.ArgAt<long>(1),
                T0, null, 1, 8, null, true, null, [], null, TableCacheOfflinePolicy.FailClosed));

        var cluster = new StubClusterStateProvider
        {
            OnTryAcquireLease = (_, _, _) => new ValueTask<IDistributedLease?>(new StubLease())
        };

        var product = new DataProduct(
            Guid.NewGuid(), TenantA, "sales", "definer_view", DataProductKind.View,
            "SELECT id FROM crm.public.orders", ProductDefinitions.Hash("SELECT id FROM crm.public.orders"),
            [OrdersTable], [new DataProductColumn("id", "bigint", ProductColumnClass.NonPii, [new ColumnLineage(OrdersTable, "id")], null, null)],
            AdminSid, ProductCreatorAuthority.DataProductAdmin, DataProductStatus.Active, null,
            new LoadSettings(LoadMode.OnDemand, TtlMinutes: 15), ProductLimits.Default, null,
            OwnerGroupSid: DomainOwnerGroup, DefaultClass: ProtectionClass.Confidential, Visibility: CatalogVisibilityMode.OnRequest,
            Version: 1, CreatedAt: T0, UpdatedAt: T0);
        await store.CreateProductAsync(product, new DataProductVersion(product.Id, 1, product.DefinitionHash, product.Definition, AdminSid, product.Authority, T0));

        var executor = new CaptureDefinerExecutor();
        var sc = new ServiceCollection();
        sc.AddSingleton<IProductDefinerExecutor>(executor);
        using var sp = sc.BuildServiceProvider();
        var runner = new FederationProductQueryRunner(sp.GetRequiredService<IServiceScopeFactory>());

        var loader = new ProductLoadService(store, runner, cacheStore, Substitute.For<ITableCacheCatalog>(), cluster, new RecordingAuditWriter(), new FakeTimeProvider(T0));
        var result = await loader.GetResultAsync(product);

        result.Stale.ShouldBeFalse();
        executor.Captured.ShouldNotBeNull();
        executor.Captured.Product.Id.ShouldBe(product.Id);
        executor.Captured.Product.OwnerGroupSid.ShouldBe(DomainOwnerGroup);
    }

    [Fact]
    public async Task PutAsync_Accepts_Canonical_OnRequest_And_Rejects_Invalid_Enum_With_422()
    {
        var store = new InMemoryDataProductRepository();
        var domainId = Guid.NewGuid();
        var ordersGuid = Guid.NewGuid();
        var domainRepo = new StubDomainRepo(domainId, ordersGuid);
        var tableRepo = new InMemoryTableMetadataRepository(new TableMetadata { Identifier = OrdersTable, Table = new Table { Id = ordersGuid, IsActive = true } });
        var ownershipRepo = new InMemoryContainerOwnership();
        var audit = new RecordingAuditWriter();
        var caps = new GovernanceCapabilities(domainRepository: domainRepo, productRepository: store);
        var authority = new ProductAuthorityService(ownershipRepo, store, new FakeTimeProvider(T0), caps, domainRepo, tableRepo);
        var service = new DataProductService(
            store, authority, new StubCatalogColumns(), FastSqlEngine.Default, ownershipRepo, audit,
            timeProvider: new FakeTimeProvider(T0),
            downgradeGuard: new ViewDowngradeGuard(caps, audit, new FakeTimeProvider(T0)));

        static HttpContext BuildPutHttp(string json)
        {
            var sec = Context(AdminSid, "DataProductAdmin");
            var ctx = new DefaultHttpContext { User = sec.Principal! };
            ctx.Items[SecurityPrincipalContext.ItemKey] = sec;
            ctx.Items[SecurityPrincipalContext.TenantIdItemKey] = TenantA;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            ctx.Request.ContentType = "application/json";
            return ctx;
        }

        var okRes = await ProductEndpoints.PutAsync(
            "sales",
            "orders_on_request",
            BuildPutHttp($$"""{"kind":"View","definition":"SELECT id, total FROM crm.public.orders","ownerGroupSid":"{{DomainOwnerGroup.Value}}","defaultClass":"CONFIDENTIAL","visibility":"ON_REQUEST"}"""),
            service);
        (okRes as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var stored = await store.GetProductAsync(TenantA, "sales", "orders_on_request");
        stored.ShouldNotBeNull();
        stored.DefaultClass.ShouldBe(ProtectionClass.Confidential);
        stored.Visibility.ShouldBe(CatalogVisibilityMode.OnRequest);

        var badVisibility = await ProductEndpoints.PutAsync(
            "sales",
            "orders_bad_vis",
            BuildPutHttp("""{"kind":"View","definition":"SELECT id, total FROM crm.public.orders","visibility":"INVALID_VISIBILITY"}"""),
            service);
        (badVisibility as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);

        var badClass = await ProductEndpoints.PutAsync(
            "sales",
            "orders_bad_class",
            BuildPutHttp("""{"kind":"View","definition":"SELECT id, total FROM crm.public.orders","defaultClass":"INVALID_CLASS"}"""),
            service);
        (badClass as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
    }
}
