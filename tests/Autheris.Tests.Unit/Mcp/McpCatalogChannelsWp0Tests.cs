namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WP-0 (PLAN-GOV-111 A-0.3, A-0.6, A-0.7, A-0.8): the MCP channels (resources, tools, <c>invoke_api</c>, dataset tools) use the one caller
/// class decided from the principal, consent-based visibility and the tenant-confined administrator view.
/// T-W0-4, T-W0-5, T-W0-6 (MCP part), T-W0-10, M-5 (MCP <c>Matches</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class McpCatalogChannelsWp0Tests
{
    private static readonly TableMetadata OrdersA = Wp0.Table("sales", "orders_a", "Orders of tenant A", ["id", "amount", "secret_note"], baseUrl: "https://a.internal/api");
    private static readonly TableMetadata InvoicesB = Wp0.Table("sales", "invoices_b", "Invoices of tenant B", ["id", "total"], baseUrl: "https://b.internal/api");

    private static McpSessionContext Session(string sid, string tenant, params string[] roles) =>
        new("s-" + sid, sid, tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: sid, Roles: roles, GroupSids: []);

    private sealed record Harness(
        McpResourceProvider Resources, ApiDispatcherService Dispatcher, McpToolExecutionHandler Tools, McpDatasetCatalog Datasets);

    private static Harness Create(GatewayOptions options, params Consent[] consents)
    {
        var tables = Wp0.Tables(OrdersA, InvoicesB);
        var consentRepo = Wp0.Consents(consents);
        var discovery = Wp0.Service(tables, consentRepo, options);
        var gatewayOptions = Options.Create(options);
        var dispatcher = new ApiDispatcherService(catalogDiscoveryService: discovery, gatewayOptions: gatewayOptions);
        var tools = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            dispatcher,
            Substitute.For<ILineageGraphStore>(),
            tables,
            gatewayOptions: gatewayOptions);
        var datasets = new McpDatasetCatalog(tables, consentRepo, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(), options: gatewayOptions);
        return new Harness(new McpResourceProvider(discovery, dispatcher), dispatcher, tools, datasets);
    }

    private static async Task<string> ReadAsync(Harness h, McpSessionContext session, string uri) =>
        (await h.Resources.ReadAsync(uri, session)).Text;

    // Table rows of the datasources resource (one per data source), independent of wording.
    private static int DatasourceRows(string markdown) => markdown.Split('\n').Count(l => l.StartsWith("| `", StringComparison.Ordinal));

    private static Task<string> ToolAsync(Harness h, McpSessionContext session, string tool, string args = "{}") =>
        h.Tools.ExecuteToolAsync(new McpToolDefinition(tool, "t", "{}", tool), args, session);

    private static Consent AllowA(string sid) => Wp0.Allow(OrdersA.Identifier, sid, Wp0.TenantA, "id", "amount");

    // T-W0-4
    [Fact]
    public async Task CatalogSummaryResource_FollowsTheCallerClassOfThePrincipal()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));

        var consented = await ReadAsync(h, Session("S-A-USER", "tenant-a"), "autheris://catalog/summary");
        consented.ShouldContain("orders_a");
        consented.ShouldNotContain("invoices_b");

        var unconsented = await ReadAsync(h, Session("S-B-USER", "tenant-b"), "autheris://catalog/summary");
        unconsented.ShouldNotContain("orders_a");
        unconsented.ShouldNotContain("invoices_b");

        var cluster = await ReadAsync(h, Session("root", "tenant-a", "ClusterAdmin"), "autheris://catalog/summary");
        cluster.ShouldContain("orders_a");
        cluster.ShouldContain("invoices_b");

        // A tenant-prefixed role string is tenant-scoped, never cluster-wide.
        var prefixed = await ReadAsync(h, Session("S-B-USER", "tenant-b", "tenant:ClusterAdmin"), "autheris://catalog/summary");
        prefixed.ShouldNotContain("orders_a");
        prefixed.ShouldNotContain("invoices_b");
    }

    [Fact]
    public async Task SchemaResource_UnconsentedTable_IsNotFound_ConsentedShowsOnlyConsentedColumns()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));
        const string uri = "autheris://catalog/datasets/sales.dbo.orders_a/schema";

        var own = await ReadAsync(h, Session("S-A-USER", "tenant-a"), uri);
        own.ShouldContain("amount");
        own.ShouldNotContain("secret_note");

        await Should.ThrowAsync<McpResourceNotFoundException>(() => h.Resources.ReadAsync(uri, Session("S-B-USER", "tenant-b")));
    }

    [Fact]
    public async Task AnonymousMcpClient_SeesNothing_EvenWithAConsentOnThatSid()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), Wp0.Allow(OrdersA.Identifier, "ANONYMOUS_MCP_CLIENT", Wp0.TenantA));

        (await ReadAsync(h, Session("ANONYMOUS_MCP_CLIENT", "tenant-a"), "autheris://catalog/summary")).ShouldNotContain("orders_a");
    }

    [Fact]
    public async Task InvokeApi_CatalogRoutes_UseThePrincipalOfTheContext_AndDefaultContextSeesNothing()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));
        var user = Wp0.Context(Wp0.User("S-A-USER", "tenant-a"));

        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET", context: user)).ShouldContain("orders_a");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET", context: user)).ShouldNotContain("invoices_b");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET")).ShouldContain("\"count\": 0");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets/sales.dbo.invoices_b", "GET", context: user)).ShouldContain("\"status\": 404");
        // Role strings in the context never decide the class.
        var spoof = user with { Roles = ["ClusterAdmin", "GovernanceAdmin"] };
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET", context: spoof)).ShouldNotContain("invoices_b");
    }

    // T-W0-5
    [Fact]
    public async Task ListDatasources_AllChannels_AreAdminOnly()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));
        var user = Session("S-A-USER", "tenant-a");
        var cluster = Session("root", "tenant-a", "ClusterAdmin");
        var claimlessAdmin = new McpSessionContext("s", "S-ADM", "legacy-single-tenant", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-ADM", Roles: ["GovernanceAdmin"], GroupSids: []);

        var forbidden = await ToolAsync(h, user, McpDatasetTools.ListDatasources);
        forbidden.ShouldContain("Forbidden");
        forbidden.ShouldNotContain("a.internal");
        (await ToolAsync(h, claimlessAdmin, McpDatasetTools.ListDatasources)).ShouldContain("Gateway:Tenancy:Mode=Single");
        (await ToolAsync(h, cluster, McpDatasetTools.ListDatasources)).ShouldContain("a.internal");

        DatasourceRows(await ReadAsync(h, user, "autheris://catalog/datasources")).ShouldBe(0);
        DatasourceRows(await ReadAsync(h, cluster, "autheris://catalog/datasources")).ShouldBe(2);

        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasources", "GET", context: Wp0.Context(Wp0.User("S-A-USER", "tenant-a"))))
            .ShouldContain("\"status\": 403");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasources", "GET")).ShouldContain("\"status\": 403");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasources", "GET", context: Wp0.Context(Wp0.User("root", null, ["ClusterAdmin"]))))
            .ShouldContain("a.internal");
    }

    [Fact]
    public async Task SearchAndGetMyPermissionsTools_ShowOnlyConsentedColumns_AndSameAnswerForUnknownAndUnconsented()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));
        var user = Session("S-A-USER", "tenant-a");
        var other = Session("S-B-USER", "tenant-b");

        var perms = await ToolAsync(h, user, McpDatasetTools.GetMyPermissions, """{"dataset":"sales.dbo.orders_a"}""");
        perms.ShouldContain("amount");
        perms.ShouldNotContain("secret_note");

        var unconsented = await ToolAsync(h, other, McpDatasetTools.GetMyPermissions, """{"dataset":"sales.dbo.orders_a"}""");
        var unknown = await ToolAsync(h, other, McpDatasetTools.GetMyPermissions, """{"dataset":"sales.dbo.nothing"}""");
        JsonDocument.Parse(unconsented).RootElement.GetProperty("message").GetString()
            .ShouldBe(JsonDocument.Parse(unknown).RootElement.GetProperty("message").GetString());

        var search = await ToolAsync(h, user, McpDatasetTools.SearchCatalog, """{"query":"orders"}""");
        search.ShouldContain("orders_a");
        search.ShouldNotContain("secret_note");
        (await ToolAsync(h, other, McpDatasetTools.SearchCatalog, """{"query":"orders"}""")).ShouldNotContain("orders_a");
    }

    // T-W0-6 (MCP part)
    [Fact]
    public async Task TenantScopedGovernanceAdmin_Multi_HasNoCrossTenantMcpView()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi),
            Wp0.Allow(InvoicesB.Identifier, "S-B-OWNER", Wp0.TenantB), Wp0.Allow(OrdersA.Identifier, "S-A-OWNER", Wp0.TenantA));
        var adminB = Wp0.User("S-B-ADMIN", "tenant-b", ["GovernanceAdmin"]);

        (await h.Datasets.ListDatasetsAsync(adminB)).Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.invoices_b"]);
        await Should.ThrowAsync<TableNotFoundException>(() => h.Datasets.DescribeDatasetAsync(adminB, "sales.dbo.orders_a"));
        await Should.ThrowAsync<TableNotFoundException>(() => h.Datasets.SampleRowsAsync(adminB, "sales.dbo.orders_a", 3, synthetic: true));

        var resource = await ReadAsync(h, Session("S-B-ADMIN", "tenant-b", "GovernanceAdmin"), "autheris://catalog/summary");
        resource.ShouldContain("invoices_b");
        resource.ShouldNotContain("orders_a");
    }

    [Fact]
    public async Task SingleMode_GovernanceAdminWithoutTenantClaim_AndClusterAdmin_SeeEverything()
    {
        var h = Create(Wp0.Options(TenancyMode.Single));

        (await h.Datasets.ListDatasetsAsync(Wp0.User("gov", null, ["GovernanceAdmin"]))).Datasets.Count.ShouldBe(2);
        (await h.Datasets.ListDatasetsAsync(Wp0.User("root", "tenant-a", ["ClusterAdmin"]))).Datasets.Count.ShouldBe(2);
        // A multi-tenant installation mistakenly set to Single still confines a GovernanceAdmin that carries a real tenant claim.
        (await h.Datasets.ListDatasetsAsync(Wp0.User("gov", "tenant-b", ["GovernanceAdmin"]))).Datasets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimlessGovernanceAdmin_Multi_IsNotAnAdministratorInTheDatasetTools()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi));

        (await h.Datasets.ListDatasetsAsync(Wp0.User("gov", null, ["GovernanceAdmin"]))).Datasets.ShouldBeEmpty();
    }

    // M-5 baseline: MCP Matches searches only the already filtered table, so a hidden column name finds nothing.
    [Fact]
    public async Task ListDatasets_SearchForAHiddenColumnName_FindsNothing()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-A-USER"));
        var user = Wp0.User("S-A-USER", "tenant-a");

        (await h.Datasets.ListDatasetsAsync(user, search: "secret_note", domain: null)).Datasets.ShouldBeEmpty();
        (await h.Datasets.ListDatasetsAsync(user, search: "amount", domain: null)).Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.orders_a"]);
    }

    // T-W0-10: the synthetic sample_rows guard is positive: administrator view or an active table consent, nothing else.
    [Fact]
    public async Task SampleRowsSynthetic_ReleasedFlagWithoutConsent_IsRefused_ConsentAndAdminAreAllowed()
    {
        var released = OrdersA with
        {
            Table = OrdersA.Table with { Visibility = DatasetVisibility.Summary },
            ReleaseProfileToCatalogViewers = true
        };
        var tables = Wp0.Tables(released);
        var consents = Wp0.Consents(Wp0.Allow(released.Identifier, "S-GRANTED", Wp0.TenantA));
        var options = Options.Create(Wp0.Options(TenancyMode.Multi, "sales.dbo.orders_a"));
        var catalog = new McpDatasetCatalog(tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(), options: options);

        // The tier branch of the visibility filter lists the table for a consent-less caller; the guard must still refuse it.
        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            catalog.SampleRowsAsync(Wp0.User("S-NO-CONSENT", "tenant-a"), "sales.dbo.orders_a", 3, synthetic: true));

        (await catalog.SampleRowsAsync(Wp0.User("S-GRANTED", "tenant-a"), "sales.dbo.orders_a", 3, synthetic: true)).IsSynthetic.ShouldBeTrue();
        (await catalog.SampleRowsAsync(Wp0.User("S-ADM", "tenant-a", ["GovernanceAdmin"]), "sales.dbo.orders_a", 3, synthetic: true)).IsSynthetic.ShouldBeTrue();
    }

    // T-W0-15: a forward-auth identity claiming ClusterAdmin never becomes a cluster admin through the real MCP caller resolution.
    [Fact]
    public async Task ForwardAuthClusterAdminClaim_ThroughRealMcpCallerResolution_TakesTheConsentPath()
    {
        var h = Create(Wp0.Options(TenancyMode.Multi), AllowA("S-FWD"));
        var forwardAuth = Wp0.User("S-FWD", "tenant-a", ["ClusterAdmin"], authType: "ForwardAuth");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = forwardAuth };

        var caller = Autheris.Api.Endpoints.McpEndpoints.ResolveCaller(http, allowOpenMcp: false);
        var session = new McpSessionContext(
            "s", caller.PrincipalId, caller.TenantId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, caller.UserSid, caller.Roles, caller.GroupSids);

        caller.Roles.ShouldNotContain("ClusterAdmin");
        var summary = await ReadAsync(h, session, "autheris://catalog/summary");
        summary.ShouldContain("orders_a");
        summary.ShouldNotContain("invoices_b");
        (await ToolAsync(h, session, McpDatasetTools.ListDatasources)).ShouldContain("Forbidden");
        (await h.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET", context: Wp0.Context(McpProtocolHandler.BuildPrincipalFromSession(session)!)))
            .ShouldNotContain("invoices_b");
    }
}
