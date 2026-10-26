namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Unit.Dbt;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WP-DT-3 (DT-A-7, DT-SC-08): the default tenant D is the catalog owner. Its GovernanceAdmin sees the whole physical catalog metadata; every
/// other tenant stays confined; a principal without tenant and without D has no catalog standing. The widening never reaches a tenant-scoped
/// object (virtual tables, semantic catalog, consents, audit).
/// </summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantCatalogScopeTests
{
    private static readonly TenantId D = new("tenant-d");
    private static readonly TenantId A = Wp0.TenantA;
    private static readonly TenantId B = Wp0.TenantB;

    private static readonly TableMetadata Orders = Wp0.Table("sales", "orders");
    private static readonly TableMetadata Invoices = Wp0.Table("finance", "invoices");
    private static readonly TableMetadata Countries = Wp0.Table("ref", "countries");

    private static TableMetadata Virtual(TenantId tenant, string name = "m")
    {
        var id = SemanticVirtualTables.IdentifierFor(tenant, name);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = name,
                DocumentationSource = SemanticVirtualTables.SourceMarker,
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };
    }

    private static GatewayOptions OptionsWithDefault(string? defaultTenant, params string[] shared) => new()
    {
        Tenancy = new TenancyOptions { DefaultTenantId = defaultTenant },
        Catalog = new Autheris.Domain.Options.DataCatalogOptions { SharedTables = shared },
        Rebac = new RebacOptions { Enabled = false }
    };

    private static async Task<string[]> StaticVisibleAsync(
        ClaimsPrincipal user, TenantId channelTenant, TenantId? owner, IEnumerable<TableMetadata> tables, params Consent[] consents)
    {
        var visible = await CatalogVisibility.VisibleTablesAsync(
            tables.ToList(), user, channelTenant, Wp0.Consents(consents), contract: null, owner, sharedTables: null, default);
        return visible.Select(t => t.Identifier.ToString()).Order().ToArray();
    }

    private static string[] Ids(params TableMetadata[] tables) => tables.Select(t => t.Identifier.ToString()).Order().ToArray();

    // ---- Scope matrix (DT-A-7) ----------------------------------------------------------------------------------------------------

    [Fact]
    public void GovernanceAdmin_Default_Tenant_Is_CatalogOwner_Claimless_And_With_Claim()
    {
        CallerScopeResolver.Resolve(Wp0.User("ga", null, ["GovernanceAdmin"]), D).ShouldBe(CallerScope.CatalogOwner);
        CallerScopeResolver.Resolve(Wp0.User("ga", D.Value, ["GovernanceAdmin"]), D).ShouldBe(CallerScope.CatalogOwner);
    }

    [Fact]
    public void GovernanceAdmin_Other_Tenant_Is_Tenant_Scope_Never_Owner()
        => CallerScopeResolver.Resolve(Wp0.User("ga", "tenant-a", ["GovernanceAdmin"]), D).ShouldBe(CallerScope.Tenant);

    [Fact]
    public void NonAdmin_In_Default_Tenant_Is_Subject_Not_Owner()
    {
        CallerScopeResolver.Resolve(Wp0.User("u", null), D).ShouldBe(CallerScope.Subject);
        CallerScopeResolver.Resolve(Wp0.User("u", D.Value), D).ShouldBe(CallerScope.Subject);
    }

    [Fact]
    public void Claimless_Scope_Is_None_Without_Default()
    {
        CallerScopeResolver.Resolve(Wp0.User("ga", null, ["GovernanceAdmin"]), (TenantId?)null).ShouldBe(CallerScope.None);
        CallerScopeResolver.Resolve(Wp0.User("u", null), (TenantId?)null).ShouldBe(CallerScope.None);
    }

    [Theory]
    [InlineData(TenantResolutionOutcome.Unresolved)]
    [InlineData(TenantResolutionOutcome.Invalid)]
    [InlineData(TenantResolutionOutcome.Unauthenticated)]
    public void Resolution_Without_Tenant_Is_None_Even_For_Default_Tenant_Admin(TenantResolutionOutcome outcome)
        => CallerScopeResolver.Resolve(Wp0.User("ga", null, ["GovernanceAdmin"]), new TenantResolution(outcome, null), D).ShouldBe(CallerScope.None);

    [Theory]
    [InlineData(TenantResolutionOutcome.FromClaim)]
    [InlineData(TenantResolutionOutcome.FromRealm)]
    [InlineData(TenantResolutionOutcome.DefaultAssigned)]
    public void Resolution_With_Tenant_Of_Any_Source_Gives_The_Same_Owner_Scope(TenantResolutionOutcome outcome)
        => CallerScopeResolver.Resolve(Wp0.User("ga", null, ["GovernanceAdmin"]), new TenantResolution(outcome, D), D).ShouldBe(CallerScope.CatalogOwner);

    [Fact]
    public void Invalid_Claim_Is_None_Even_With_Default()
        => CallerScopeResolver.Resolve(Wp0.User("ga", "bad tenant!!", ["GovernanceAdmin"]), D).ShouldBe(CallerScope.None);

    [Fact]
    public void ClusterAdmin_Is_Global_With_And_Without_Default()
    {
        CallerScopeResolver.Resolve(Wp0.User("root", null, ["ClusterAdmin"]), D).ShouldBe(CallerScope.Global);
        CallerScopeResolver.Resolve(Wp0.User("root", null, ["ClusterAdmin"]), (TenantId?)null).ShouldBe(CallerScope.Global);
    }

    // ---- Physical catalog (CatalogVisibility engine: Arrow, Trino, Iceberg, MCP, explanation) --------------------------------------

    [Fact]
    public async Task GovernanceAdmin_Default_Tenant_Sees_Whole_Catalog()
    {
        var claimless = await StaticVisibleAsync(Wp0.User("ga", null, ["GovernanceAdmin"]), D, D, [Orders, Invoices, Countries]);
        var withClaim = await StaticVisibleAsync(Wp0.User("ga", D.Value, ["GovernanceAdmin"]), D, D, [Orders, Invoices, Countries]);

        claimless.ShouldBe(Ids(Orders, Invoices, Countries));
        withClaim.ShouldBe(Ids(Orders, Invoices, Countries));
    }

    [Fact]
    public async Task GovernanceAdmin_Other_Tenant_Confined()
    {
        var visible = await StaticVisibleAsync(
            Wp0.User("ga", "tenant-a", ["GovernanceAdmin"]), A, D, [Orders, Invoices, Countries],
            Wp0.Allow(Orders.Identifier, "S-OWNER", A));

        visible.ShouldBe(Ids(Orders));
    }

    [Fact]
    public async Task Claimless_Without_Default_Sees_Nothing_Whatever_The_Role()
    {
        var consents = new[] { Wp0.Allow(Orders.Identifier, "S-X", TenantId.LegacySingleTenant) };

        (await StaticVisibleAsync(Wp0.User("S-X", null, ["GovernanceAdmin"]), TenantId.LegacySingleTenant, null, [Orders], consents)).ShouldBeEmpty();
        (await StaticVisibleAsync(Wp0.User("S-X", null), TenantId.LegacySingleTenant, null, [Orders], consents)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DT_SC_08_Default_Tenant_Foreign_Virtual_Tables_Dropped()
    {
        var ownVirtual = Virtual(D, "own_model");
        var foreignVirtual = Virtual(A, "foreign_model");

        var visible = await StaticVisibleAsync(Wp0.User("ga", null, ["GovernanceAdmin"]), D, D, [Orders, ownVirtual, foreignVirtual]);

        visible.ShouldBe(Ids(Orders, ownVirtual));
    }

    [Fact]
    public async Task DT_SC_08_Other_Tenant_GA_Cannot_See_Default_Tenant_Scoped_Objects()
    {
        var defaultVirtual = Virtual(D, "d_model");
        var ownVirtual = Virtual(A, "a_model");

        var visible = await StaticVisibleAsync(
            Wp0.User("ga", "tenant-a", ["GovernanceAdmin"]), A, D, [Orders, defaultVirtual, ownVirtual],
            Wp0.Allow(Orders.Identifier, "S-OWNER", A), Wp0.Allow(ownVirtual.Identifier, "S-OWNER", A));

        visible.ShouldBe(Ids(Orders, ownVirtual));
    }

    [Fact]
    public async Task Default_Tenant_Subject_Uses_Only_Consents_Of_Its_Own_Tenant()
    {
        var consents = new[]
        {
            Wp0.Allow(Orders.Identifier, "S-U", D, "id"),
            Wp0.Allow(Invoices.Identifier, "S-U", A, "id")
        };

        var visible = await StaticVisibleAsync(Wp0.User("S-U", D.Value), D, D, [Orders, Invoices], consents);

        visible.ShouldBe(Ids(Orders));
    }

    // ---- One visibility service (REST, GraphQL, MCP) ------------------------------------------------------------------------------

    [Theory]
    [InlineData(CatalogChannel.Rest)]
    [InlineData(CatalogChannel.GraphQl)]
    [InlineData(CatalogChannel.Mcp)]
    public async Task DT_SC_08_Default_Tenant_Widening_Is_Metadata_Only_Across_Channels(CatalogChannel channel)
    {
        var tables = Wp0.Tables(Orders, Invoices, Countries, Virtual(D), Virtual(A), Virtual(B));
        var service = CatalogVisibilityTestFactory.Create(tables, Wp0.Consents(), OptionsWithDefault(D.Value));

        var owner = await service.GetVisibleAsync(new CatalogCaller(Wp0.User("ga", null, ["GovernanceAdmin"]), D, channel));

        owner.Scope.ShouldBe(CallerScope.CatalogOwner);
        owner.Tables.Select(t => t.Identifier.ToString()).Order().ShouldBe(Ids(Orders, Invoices, Countries, Virtual(D)));
    }

    [Fact]
    public async Task DT_SC_08_Default_Tenant_GA_Cannot_Read_Other_Tenant_Scoped_Objects_Mixed()
    {
        var tables = Wp0.Tables(Orders, Virtual(D, "d"), Virtual(A, "a"));
        var service = CatalogVisibilityTestFactory.Create(tables, Wp0.Consents(Wp0.Allow(Orders.Identifier, "S-A", A)), OptionsWithDefault(D.Value));

        var visible = await service.GetVisibleAsync(new CatalogCaller(Wp0.User("ga", D.Value, ["GovernanceAdmin"]), D, CatalogChannel.Rest));

        visible.Tables.ShouldNotContain(t => t.Identifier == Virtual(A, "a").Identifier);
        // Even with the channel naming the foreign tenant, the principal's own tenant decides the class and the virtual-table drop.
        var spoofed = await service.GetVisibleAsync(new CatalogCaller(Wp0.User("ga", D.Value, ["GovernanceAdmin"]), A, CatalogChannel.Rest));
        spoofed.Tables.ShouldNotContain(t => t.Identifier == Virtual(A, "a").Identifier);
    }

    [Fact]
    public async Task DT_SC_08_No_Cross_Tenant_Leakage_Mixed_Deployment()
    {
        var vD = Virtual(D, "d");
        var vA = Virtual(A, "a");
        var vB = Virtual(B, "b");
        var tables = Wp0.Tables(Orders, Invoices, Countries, vD, vA, vB);
        var consents = Wp0.Consents(
            Wp0.Allow(Orders.Identifier, "S-A-GA", A),
            Wp0.Allow(vA.Identifier, "S-A-GA", A),
            Wp0.Allow(Invoices.Identifier, "S-B-GA", B),
            Wp0.Allow(vB.Identifier, "S-B-GA", B));
        var service = CatalogVisibilityTestFactory.Create(tables, consents, OptionsWithDefault(D.Value));

        async Task<string[]> For(string sid, string? tenant, TenantId channel) =>
            (await service.GetVisibleAsync(new CatalogCaller(Wp0.User(sid, tenant, ["GovernanceAdmin"]), channel, CatalogChannel.Rest)))
                .Tables.Select(t => t.Identifier.ToString()).Order().ToArray();

        (await For("S-D-GA", null, D)).ShouldBe(Ids(Orders, Invoices, Countries, vD));
        (await For("S-A-GA", "tenant-a", A)).ShouldBe(Ids(Orders, vA));
        (await For("S-B-GA", "tenant-b", B)).ShouldBe(Ids(Invoices, vB));
    }

    [Fact]
    public async Task Claimless_Admin_Without_Default_Gets_The_Empty_Set_From_The_Service()
    {
        var service = CatalogVisibilityTestFactory.Create(Wp0.Tables(Orders), Wp0.Consents(Wp0.Allow(Orders.Identifier, "ga", TenantId.LegacySingleTenant)), OptionsWithDefault(null));

        var visible = await service.GetVisibleAsync(new CatalogCaller(Wp0.User("ga", null, ["GovernanceAdmin"]), TenantId.LegacySingleTenant, CatalogChannel.Rest));

        visible.Tables.ShouldBeEmpty();
    }

    // ---- Tenant-scoped objects stay confined (semantic catalog, schema names, audit) ---------------------------------------------------

    [Fact]
    public void Virtual_Table_Schema_Is_Tenant_Keyed_For_The_Default_Tenant_Too()
    {
        SemanticVirtualTables.SchemaFor(D).ShouldBe(SemanticVirtualTables.TenantSchema(D));
        SemanticVirtualTables.SchemaFor(D).ShouldNotBe(SemanticVirtualTables.SchemaFor(A));
        SemanticVirtualTables.BelongsToTenant(Virtual(D), D).ShouldBeTrue();
        SemanticVirtualTables.BelongsToTenant(Virtual(D), A).ShouldBeFalse();
    }

    [Fact]
    public async Task DT_SC_08_Semantic_Catalog_Is_Keyed_By_Tenant_Also_For_The_Default_Tenant()
    {
        var options = Options.Create(OptionsWithDefault(D.Value));
        var catalog = new InMemorySemanticCatalog(options);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m_a"), A);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m_d"), D);

        (await catalog.ListMetricsAsync(null, D)).Select(m => m.Name).ShouldBe(["m_d"]);
        (await catalog.ListMetricsAsync(null, A)).Select(m => m.Name).ShouldBe(["m_a"]);
        (await catalog.ListMetricsAsync(null, B)).ShouldBeEmpty();
    }

    [Fact]
    public void SemanticTenancy_Caller_Tenant_Follows_The_Resolution()
    {
        var tenancy = SemanticTenancy.FromOptions(Options.Create(OptionsWithDefault(D.Value)));
        var noDefault = SemanticTenancy.FromOptions(Options.Create(OptionsWithDefault(null)));

        tenancy.ResolveCallerTenant(Wp0.User("u", null)).ShouldBe(D);
        tenancy.ResolveCallerTenant(Wp0.User("u", "tenant-a")).ShouldBe(A);
        noDefault.ResolveCallerTenant(Wp0.User("u", null)).ShouldBeNull();
        tenancy.IsCatalogOwner(D).ShouldBeTrue();
        tenancy.IsCatalogOwner(A).ShouldBeFalse();
        noDefault.IsCatalogOwner(D).ShouldBeFalse();
    }

    [Fact]
    public async Task DT_SC_08_Default_Tenant_GA_Audit_Query_Is_Own_Tenant_Only()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        AuditLogPageQuery? seen = null;
        repo.QueryAuditLogPageAsync(Arg.Do<AuditLogPageQuery>(q => seen = q), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([], null));
        HttpContext Context()
        {
            var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            ctx.User = Wp0.User("S-D-GA", null, ["GovernanceAdmin"]);
            ctx.Items[TenantResolutionMiddleware.TenantIdItemKey] = D;
            return ctx;
        }

        await GovernanceEndpoints.ListAuditPageAsync(Context(), repo, null, null, null, null, null, null);
        seen.ShouldNotBeNull();
        seen!.Tenant.ShouldBe(D);

        seen = null;
        var foreign = await GovernanceEndpoints.ListAuditPageAsync(Context(), repo, null, null, null, null, null, A.Value);
        seen.ShouldBeNull();
        var render = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        render.Response.Body = new MemoryStream();
        await foreign.ExecuteAsync(render);
        render.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
