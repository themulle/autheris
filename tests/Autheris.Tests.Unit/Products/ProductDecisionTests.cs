using Autheris.Application.Policy.Pdp;
using Autheris.Application.Products;
using Autheris.Application.Services;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductDecisionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view");

    private readonly ProductHarness _h = new();

    private static SecurityPrincipalContext Subject(Sid sid) => new()
    {
        IsAuthenticated = true,
        UserSid = sid,
        ResolvedTenant = ProductHarness.Tenant,
        GroupSids = new HashSet<Sid>(),
        GroupResolutionStatus = GroupResolutionStatus.Complete,
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer"
    };

    private static Consent Grant(TableIdentifier table, ConsentEffect effect, params ConsentColumnRule[] rules) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = ProductHarness.Tenant,
        TableIdentifier = table,
        GranteeType = GranteeType.User,
        GranteeSid = ProductHarness.Carol,
        Effect = effect,
        ValidFrom = Now.AddDays(-1),
        ValidTo = Now.AddDays(30),
        ColumnRules = rules
    };

    private static ConsentColumnRule Rule(string column, ColumnAccessLevel level) => new() { ColumnName = column, AccessLevel = level };

    private AccessDecisionPoint Pdp(params Consent[] consents) => new(
        Options.Create(new GatewayOptions()),
        new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
        new TestInMemoryConsentRepository(consents, filterWindow: true),
        new TestNoOpConsentCache(),
        mandatoryFilters: new NoOpMandatoryRowFilterResolver(),
        productRepository: _h.Store);

    private async Task<TableMetadata> CreateProductAsync(string sql = "SELECT c.id, c.email, c.segment FROM crm.public.customers c")
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var product = await _h.Service.PutAsync(_h.Put(admin, sql, ProductHarness.Bob));
        return ProductCatalogProjector.Project(product, Now, Now);
    }

    private static ValueTask<TableAccessDecision> Decide(AccessDecisionPoint pdp, TableMetadata table, AccessOperation op = AccessOperation.Read) =>
        pdp.DecideAsync(new AccessRequest(Subject(ProductHarness.Carol), table, op, AccessTransport.Rest));

    [Fact]
    public async Task Product_grant_suffices_without_base_grants()
    {
        var product = await CreateProductAsync();
        var pdp = Pdp(Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear), Rule("segment", ColumnAccessLevel.Clear)));

        var decision = await Decide(pdp, product);

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["segment"].ShouldBe(ColumnAccessLevel.Clear);
        decision.Table.ShouldBe(ProductObject);
    }

    [Fact]
    public async Task No_product_grant_is_default_deny()
    {
        var product = await CreateProductAsync();
        // a grant on the base table does not open the product
        var pdp = Pdp(Grant(ProductHarness.Customers, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear)));

        (await Decide(pdp, product)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Base_deny_and_hard_policy_do_not_affect_product()
    {
        var product = await CreateProductAsync();
        var pdp = Pdp(
            Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear)),
            Grant(ProductHarness.Customers, ConsentEffect.Deny));

        (await Decide(pdp, product)).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Deny_on_product_wins()
    {
        var product = await CreateProductAsync();
        var pdp = Pdp(
            Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear)),
            Grant(ProductObject, ConsentEffect.Deny));

        (await Decide(pdp, product)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Pii_masked_column_stays_masked_even_with_a_clear_rule()
    {
        var product = await CreateProductAsync();
        var pdp = Pdp(Grant(ProductObject, ConsentEffect.Allow,
            Rule("id", ColumnAccessLevel.Clear), Rule("email", ColumnAccessLevel.Clear), Rule("segment", ColumnAccessLevel.Clear)));

        var decision = await Decide(pdp, product);

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["email"].ShouldBe(ColumnAccessLevel.Mask);
        decision.ColumnAccess["segment"].ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Clear_eligible_needs_clear_rule_on_grant()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var created = await _h.Service.PutAsync(_h.Put(admin, "SELECT id, email FROM crm.public.customers", ProductHarness.Bob));
        await _h.Service.ClassifyColumnAsync(_h.Caller(ProductHarness.Admin, admin: true), "sales", "orders_view", "email", ProductColumnClass.PiiClearEligible, "pseudonymised upstream");
        var product = ProductCatalogProjector.Project((await _h.Store.GetProductAsync(created.Tenant, "sales", "orders_view"))!, Now, Now);

        var withClear = await Decide(Pdp(Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear), Rule("email", ColumnAccessLevel.Clear))), product);
        var withMask = await Decide(Pdp(Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear), Rule("email", ColumnAccessLevel.Mask))), product);

        withClear.ColumnAccess["email"].ShouldBe(ColumnAccessLevel.Clear);
        withMask.ColumnAccess["email"].ShouldBe(ColumnAccessLevel.Mask);
        ProductColumnAccess.Resolve(ProductColumnClass.PiiClearEligible, ColumnAccessLevel.Mask).ShouldBe(ColumnAccessLevel.Mask);
        ProductColumnAccess.Resolve(ProductColumnClass.PiiMasked, ColumnAccessLevel.Clear).ShouldBe(ColumnAccessLevel.Mask);
        ProductColumnAccess.Resolve(ProductColumnClass.NonPii, ColumnAccessLevel.Deny).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task A_product_is_read_only_and_a_retired_or_unknown_product_is_denied()
    {
        var product = await CreateProductAsync();
        var pdp = Pdp(Grant(ProductObject, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear)));

        (await Decide(pdp, product, AccessOperation.Insert)).IsAllowed.ShouldBeFalse();
        (await Decide(pdp, product with { Table = product.Table with { IsActive = false } })).IsAllowed.ShouldBeFalse();

        var other = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "missing");
        var unknown = product with { Identifier = other, Table = product.Table with { TableName = other.TableName } };
        (await Decide(Pdp(Grant(other, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear))), unknown)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exposure_follows_status_and_max_staleness()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var created = await _h.Service.PutAsync(_h.Put(admin, "SELECT id FROM crm.public.customers", ProductHarness.Bob));
        var limited = created with { Load = new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1), MaxStalenessMinutes: 60) };

        ProductFreshness.IsExposable(limited, Now, Now).ShouldBeTrue();
        ProductFreshness.IsExposable(limited with { Status = DataProductStatus.Stale }, Now.AddMinutes(-30), Now).ShouldBeTrue("stale within MaxStaleness");
        ProductFreshness.IsExposable(limited with { Status = DataProductStatus.Stale }, Now.AddMinutes(-90), Now).ShouldBeFalse("stale beyond MaxStaleness");
        ProductFreshness.IsExposable(limited with { Status = DataProductStatus.Retired }, Now, Now).ShouldBeFalse();
        ProductFreshness.IsExposable(limited with { Status = DataProductStatus.Invalid }, Now, Now).ShouldBeFalse();
        ProductCatalogProjector.Project(limited with { Status = DataProductStatus.Retired }, Now, Now).Table.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Product_catalog_object_uses_tenant_scoped_identity()
    {
        var product = await CreateProductAsync();
        var schema = Autheris.Application.Catalog.Semantic.SemanticVirtualTables.TenantSchema(ProductHarness.Tenant);

        product.Identifier.ShouldBe(new TableIdentifier("products", schema, "sales__orders_view"));
        ProductSource.CacheKey(ProductHarness.Tenant, "sales", "orders_view").ShouldBe(product.Identifier);
        ProductSource.TryParse(product.Identifier, ProductHarness.Tenant, out var parsedSchema, out var parsedName).ShouldBeTrue();
        (parsedSchema, parsedName).ShouldBe(("sales", "orders_view"));

        // another tenant, the bare legacy form and a wrong domain name no product
        ProductSource.TryParse(product.Identifier, new TenantId("tenant-b"), out _, out _).ShouldBeFalse();
        ProductSource.TryParse(new TableIdentifier("products", "sales", "orders_view"), ProductHarness.Tenant, out _, out _).ShouldBeFalse();
        ProductSource.TryParse(product.Identifier with { Domain = "erp" }, ProductHarness.Tenant, out _, out _).ShouldBeFalse();

        // a grant addressed at another tenant's product identity never opens this product
        var foreign = ProductSource.ObjectOf(new TenantId("tenant-b"), "sales", "orders_view");
        var pdp = Pdp(Grant(foreign, ConsentEffect.Allow, Rule("id", ColumnAccessLevel.Clear)));
        (await Decide(pdp, product with { Identifier = foreign })).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_schema_that_would_make_the_identity_ambiguous_is_rejected()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        foreach (var schema in new[] { "sales__eu", "sales_" })
        {
            var request = new ProductPutRequest(admin, schema, "orders_view", DataProductKind.View, "SELECT id FROM crm.public.customers", ProductHarness.Bob);
            (await Should.ThrowAsync<ProductException>(() => _h.Service.PutAsync(request))).Code.ShouldBe(ProductErrorCodes.DefinitionInvalid);
        }
    }

    [Fact]
    public async Task Product_owner_linked_as_data_owner()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var created = await _h.Service.PutAsync(_h.Put(admin, "SELECT id FROM crm.public.customers", ProductHarness.Bob));
        var catalogObject = ProductSource.ObjectOf(created.Tenant, "sales", "orders_view");

        _h.OwnerLinks.Owners[catalogObject].ShouldBe(ProductHarness.Bob);

        await _h.Service.TransferAsync(admin, "sales", "orders_view", ProductHarness.Alice);
        _h.OwnerLinks.Owners[catalogObject].ShouldBe(ProductHarness.Alice);

        await _h.Service.RetireAsync(admin, "sales", "orders_view");
        _h.OwnerLinks.Owners[catalogObject].ShouldBeNull("a retired product has no data owner");
    }

    [Fact]
    public async Task A_failed_owner_link_fails_the_operation()
    {
        var failing = new RecordingOwnerStore { Fail = true };
        var sync = new ProductOwnershipSync(failing);
        var product = await CreateProductAsync();
        var stored = (await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "orders_view"))!;

        (await Should.ThrowAsync<ProductException>(() => sync.SyncAsync(stored))).Code.ShouldBe(ProductErrorCodes.OwnerSyncFailed);
        product.ShouldNotBeNull();
    }
}
