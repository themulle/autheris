using System.Runtime.CompilerServices;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Olap;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Products;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Audit;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.TableCache;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Analysis;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>Stage 2 end to end: a caller query on a product is staged from the cached result under the caller's decision on the product object.</summary>
[Trait("Category", "Security")]
public sealed class ProductCallerQueryTests : IDisposable
{
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view");
    private static readonly FederationBudget Budget = new(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);

    private readonly ProductHarness _h = new();
    private readonly TableCacheCatalog _catalog = new();
    private readonly CountingRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly IDistributedClusterStateProvider _cluster = ProductHarness.LeasingCluster();
    private readonly InMemoryTableCacheStore _store;

    public ProductCallerQueryTests()
    {
        _store = new InMemoryTableCacheStore(Options.Create(new TableCacheOptions()), null, _catalog);
    }

    public void Dispose() => _store.Dispose();

    private sealed class CountingRunner : IProductQueryRunner
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ProductQueryResult> RunAsync(ProductDefiner definer, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProductQueryResult(["id", "email", "segment"], Rows(ct)));
        }

        private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var row in new object?[][] { [1L, "a@x.io", "gold"], [2L, "b@x.io", "silver"], [3L, "c@x.io", "gold"] })
            {
                await Task.Yield();
                yield return row;
            }
        }
    }

    private ProductLoadService Loader() => new(_h.Store, _runner, _store, _catalog, _cluster, _h.Audit, _time);

    private async Task<(DataProduct Product, ProductLoadService Loader)> LoadedProductAsync(LoadSettings? load = null)
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var product = await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", "orders_view", DataProductKind.View,
            "SELECT c.id, c.email, c.segment FROM crm.public.customers c", ProductHarness.Bob, load ?? new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1))));
        var loader = Loader();
        await loader.LoadAsync(product);
        return (product, loader);
    }

    private static Consent Grant(TableIdentifier table, params ConsentRowFilter[] filters) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = ProductHarness.Tenant,
        TableIdentifier = table,
        GranteeType = GranteeType.User,
        GranteeSid = ProductHarness.Carol,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "segment", AccessLevel = ColumnAccessLevel.Clear }
        ],
        RowFilters = filters
    };

    private static ConsentRowFilter Gold() => new() { ColumnName = "segment", Operator = "EQ", ValueType = "string", ValueJson = "\"gold\"" };

    private async Task<TableAccessDecision> DecideAsync(TableMetadata table, params Consent[] grants)
    {
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
            new TestInMemoryConsentRepository(grants, filterWindow: true),
            new TestNoOpConsentCache(),
            mandatoryFilters: new NoOpMandatoryRowFilterResolver(),
            productRepository: _h.Store);
        var subject = new SecurityPrincipalContext
        {
            IsAuthenticated = true,
            UserSid = ProductHarness.Carol,
            ResolvedTenant = ProductHarness.Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        return await pdp.DecideAsync(new AccessRequest(subject, table, AccessOperation.Read, AccessTransport.Rest));
    }

    private (FederatedStagingService Service, IDataSourceTransportGate Gate) Staging(ProductLoadService loader)
    {
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        var gate = Substitute.For<IDataSourceTransportGate>();
        var registry = Substitute.For<Autheris.Application.DataSources.Connectors.IAutherisConnectorRegistry>();
        var service = new FederatedStagingService(
            registry, masking, Substitute.For<IAuditLogRepository>(), Options.Create(new GatewayOptions()), NullLogger<FederatedStagingService>.Instance,
            transportGate: gate, productRouter: new ProductResultRouter(_h.Store, loader, _catalog), productCache: _store);
        return (service, gate);
    }

    private static StagingTableRequest Request(TableMetadata table, TableAccessDecision decision) => new(
        new TableAccessTarget(table.Identifier.Domain, table.Identifier.Schema, table.Identifier.TableName, "p", table.Identifier.ToString()),
        table, decision, "stg_product", ["id", "email", "segment"]);

    [Fact]
    public async Task Caller_query_via_websql_reads_cached_result_with_masking()
    {
        var (product, loader) = await LoadedProductAsync();
        var table = ProductCatalogProjector.Project(product, _time.GetUtcNow(), _time.GetUtcNow());
        var decision = await DecideAsync(table, Grant(ProductObject));
        var (service, gate) = Staging(loader);
        var callsAfterLoad = _runner.Calls;

        var staged = await service.StageAsync([Request(table, decision)], new System.Security.Claims.ClaimsPrincipal(), ProductHarness.Tenant, Budget);

        var rows = staged.Single().GovernedRows;
        rows.Count.ShouldBe(3);
        rows.Select(r => r["id"]).ShouldBe([1L, 2L, 3L]);
        rows.ShouldAllBe(r => (string?)r["email"] == "***", "a PII column of the product stays masked even with a clear rule");
        rows.Select(r => (string?)r["segment"]).ShouldBe(["gold", "silver", "gold"]);
        _runner.Calls.ShouldBe(callsAfterLoad, "the caller never runs the definition and never reads a base table");
        await gate.Received().EnsureCatalogSourceAsync(ProductSource.Name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_row_policy_on_product_applies()
    {
        var (product, loader) = await LoadedProductAsync();
        var table = ProductCatalogProjector.Project(product, _time.GetUtcNow(), _time.GetUtcNow());
        var decision = await DecideAsync(table, Grant(ProductObject, Gold()));
        var (service, _) = Staging(loader);

        var staged = await service.StageAsync([Request(table, decision)], new System.Security.Claims.ClaimsPrincipal(), ProductHarness.Tenant, Budget);

        staged.Single().GovernedRows.Select(r => r["id"]).ShouldBe([1L, 3L], "only the gold rows of the cached result");
    }

    [Fact]
    public async Task Caller_without_product_grant_gets_no_rows_via_websql()
    {
        var (product, loader) = await LoadedProductAsync();
        var table = ProductCatalogProjector.Project(product, _time.GetUtcNow(), _time.GetUtcNow());
        // a grant on the base table never opens the product
        var decision = await DecideAsync(table, Grant(ProductHarness.Customers));
        var (service, _) = Staging(loader);

        decision.IsAllowed.ShouldBeFalse();
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            service.StageAsync([Request(table, decision)], new System.Security.Claims.ClaimsPrincipal(), ProductHarness.Tenant, Budget));
        ex.Message.ShouldNotContain("customers");
    }

    [Fact]
    public async Task A_product_of_another_tenant_is_never_staged()
    {
        var (product, loader) = await LoadedProductAsync();
        var table = ProductCatalogProjector.Project(product, _time.GetUtcNow(), _time.GetUtcNow());
        var decision = TableAccessDecision.Allowed(ProductObject, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true);
        var (service, _) = Staging(loader);

        await Should.ThrowAsync<ProductLoadException>(() =>
            service.StageAsync([Request(table, decision)], new System.Security.Claims.ClaimsPrincipal(), new TenantId("tenant-b"), Budget));
    }

    [Fact]
    public async Task Router_stages_a_single_product_table_and_denies_a_foreign_one()
    {
        var (product, _) = await LoadedProductAsync();
        var table = ProductCatalogProjector.Project(product, _time.GetUtcNow(), _time.GetUtcNow());
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        var web = new WebSqlOptions { DefaultDataSourceName = "default", AllowedDataSources = ["crm"] };
        var router = new CrossSourceQueryRouter(repo, new CrossSourceOptions { Enabled = false }, web);
        var target = new TableAccessTarget(table.Identifier.Domain, table.Identifier.Schema, table.Identifier.TableName, null, table.Identifier.ToString());
        var query = new SqlQueryMetadata(SqlStatementType.Select, [target], ["id"], 0, 0, false, null);

        var decision = await router.RouteAsync(query, null, ProductHarness.Tenant, isDml: false);

        decision.SourceClass.ShouldBe(QuerySourceClass.CrossSource);
        decision.Tables.Single().EffectiveDataSource.ShouldBe(ProductSource.Name);
        await Should.ThrowAsync<WebSqlTableDeniedException>(() => router.RouteAsync(query, null, new TenantId("tenant-b"), isDml: false));
        await Should.ThrowAsync<WebSqlPolicyException>(() => router.RouteAsync(query, null, ProductHarness.Tenant, isDml: true));
    }

    [Fact]
    public void Guard_and_gate_accept_the_product_source_only_for_the_catalog_path()
    {
        var web = new WebSqlOptions { AllowedDataSources = ["crm"] };
        DataSourceAllowlistGuard.IsAllowed(web, ProductHarness.Tenant, ProductSource.Name).ShouldBeTrue();
        DataSourceAllowlistGuard.IsAllowed(web, ProductHarness.Tenant, "erp").ShouldBeFalse();

        var resolver = Substitute.For<IDataSourceOptionsResolver>();
        DataSourceTransportGate.Decide(resolver, ProductSource.Name, DataSourceTransportFlags.Rest, catalogKnown: true).IsAllowed.ShouldBeTrue();
        DataSourceTransportGate.Decide(resolver, ProductSource.Name, DataSourceTransportFlags.Rest, catalogKnown: false).IsAllowed.ShouldBeFalse("a client-named source is never the product source");
        DataSourceTransportGate.Decide(resolver, ProductSource.Name, DataSourceTransportFlags.None, catalogKnown: true).IsAllowed.ShouldBeFalse("an unset ingress still denies");
    }

    [Fact]
    public async Task Idle_product_result_is_dropped()
    {
        var (product, loader) = await LoadedProductAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, IdleDropMinutes: 30));
        var result = await loader.GetResultAsync(product);
        var job = new ProductIdleDropJob(_h.Store, loader, NullLogger<ProductIdleDropJob>.Instance);
        var context = new Autheris.Application.Runtime.Hosting.ClusterJobContext(1, Substitute.For<IDistributedLease>());

        await job.ExecuteAsync(context, CancellationToken.None);
        _catalog.TryGet(result.CacheKey, out _).ShouldBeTrue("a recently read result stays");

        _time.Advance(TimeSpan.FromMinutes(31));
        await job.ExecuteAsync(context, CancellationToken.None);

        _catalog.TryGet(result.CacheKey, out _).ShouldBeFalse();
        loader.TrackedOnDemand().ShouldBeEmpty();
    }
}
