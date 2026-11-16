namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// WP-0 (PLAN-GOV-111 M-12, T-W0-5, T-W0-6): the REST catalog and the MCP tools show a consent-less caller nothing, a consented caller only the
/// consented columns, and a tenant-B GovernanceAdmin nothing of tenant A, over the real pipeline (authentication, authorization policies, DI) on SQLite.
/// The test authentication handler issues a tenant claim for every request (the legacy single tenant when no tenant header is sent), so
/// a request without <c>X-Test-Tenant</c> is a principal without a real tenant claim.
/// </summary>
public sealed class CatalogChannelParityTests : IClassFixture<CatalogChannelParityTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly TableIdentifier OrdersA = new("dom_a", "main", "orders_a");
    private static readonly TableIdentifier InvoicesB = new("dom_b", "main", "invoices_b");

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();
        public WebApplicationFactory<Program> MultiFactory { get; }
        public WebApplicationFactory<Program> TierFactory { get; }
        public bool MultiSeeded { get; set; }
        public bool TierSeeded { get; set; }

        public HostFixture()
        {
            MultiFactory = Configure(Root, "Multi");
            TierFactory = Configure(Root, "Multi", SummaryT.ToString().ToLowerInvariant(), FullT.ToString().ToLowerInvariant(), HiddenT.ToString().ToLowerInvariant());
        }

        public void Dispose()
        {
            TierFactory.Dispose();
            MultiFactory.Dispose();
            Root.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private readonly HostFixture _fixture;
    private WebApplicationFactory<Program> _factory => _fixture.MultiFactory;

    public CatalogChannelParityTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    private static WebApplicationFactory<Program> Configure(
        WebApplicationFactory<Program> factory, string tenancyMode, params string[] sharedTables) =>
        factory.WithWebHostBuilder(b =>
        {
            for (var i = 0; i < sharedTables.Length; i++)
            {
                b.UseSetting($"Gateway:Catalog:SharedTables:{i}", sharedTables[i]);
            }

            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("HostFixture"));
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            if (tenancyMode == "Single")
            {
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", "default-tenant");
            }
            else
            {
                // appsettings.Development.json configures D; a Multi host has none (an empty value is rejected, so the key is nulled).
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            }

        });

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        foreach (var (id, description) in new[] { (OrdersA, "Orders of tenant A"), (InvoicesB, "Invoices of tenant B") })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = id.Domain, Description = description },
                Identifier = id,
                Columns = new List<TableColumn>
                {
                    new() { ColumnName = "id", DataType = "integer" },
                    new() { ColumnName = "amount", DataType = "integer" },
                    new() { ColumnName = "secret_note", DataType = "varchar" }
                }
            });
        }

        await ConsentAsync(repo, OrdersA, "S-A-USER", TenantA, "id", "amount");
        await ConsentAsync(repo, InvoicesB, "S-B-OWNER", TenantB);
    }

    private static async Task ConsentAsync(IGovernanceRepository repo, TableIdentifier id, string sid, string tenant, params string[] columns)
    {
        var meta = await repo.GetTableMetadataAsync(id);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId(tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(sid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = columns.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
        });
    }

    private async Task<HttpClient> ClientAsync(string sid, string? tenant, string roles, WebApplicationFactory<Program>? factory = null)
    {
        if (!_fixture.MultiSeeded && factory == null)
        {
            await SeedAsync(_factory);
            _fixture.MultiSeeded = true;
        }

        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        if (tenant != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        }

        return client;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static string WithoutRequestIdentity(string problemJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(problemJson);
        return string.Join("|", doc.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("instance" or "correlationId" or "traceId"))
            .Select(p => p.Name + "=" + p.Value));
    }

    [Fact]
    public async Task Rest_ConsentlessCaller_SeesNothing_AndDetailIsA404()
    {
        var client = await ClientAsync("S-B-USER", TenantB, "Reader");

        var list = await client.GetAsync("/api/v1/catalog/datasets");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Authorized discovery: the caller's own tenant objects are listed without access (every column Deny), a foreign tenant's table is absent.
        var listBody = await BodyAsync(list);
        listBody.ShouldNotContain("orders_a");
        MaskingStatesOf(await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets/dom_b.main.invoices_b"))).Values.ShouldAllBe(state => state == "Deny");
        (await client.GetAsync("/api/v1/catalog/datasets/dom_a.main.orders_a")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/catalog/datasets/dom_a.main.does_not_exist")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BodyAsync(await client.GetAsync("/api/v1/catalog/search?q=orders"))).ShouldBe("[]");
    }

    [Fact]
    public async Task Rest_ConsentedCaller_SeesOnlyConsentedTableAndColumns()
    {
        var client = await ClientAsync("S-A-USER", TenantA, "Reader");

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("orders_a");
        list.ShouldNotContain("invoices_b");

        var detail = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets/dom_a.main.orders_a"));
        var states = MaskingStatesOf(detail);
        states["id"].ShouldBe("Clear");
        states["amount"].ShouldBe("Clear");
        states["secret_note"].ShouldBe("Deny");

        var search = await BodyAsync(await client.GetAsync("/api/v1/catalog/search?q=secret_note"));
        search.ShouldNotContain("secret_note");
    }

    [Fact]
    public async Task Datasources_AdminOnly_NonAdmin403_LegacyIdTenantAdminConfined_TenantAdminConfined_ClusterAdminSeesAll()
    {
        (await (await ClientAsync("S-A-USER", TenantA, "Reader")).GetAsync("/api/v1/catalog/datasources")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // WP-DT-4: a truly tenant-less principal (Multi, no default tenant) is refused at the gate (S6); the legacy id carried as an explicit
        // tenant claim is an ordinary tenant, so its GovernanceAdmin is a confined tenant administrator.
        (await (await ClientAsync("S-ADM", null, "GovernanceAdmin")).GetAsync("/api/v1/catalog/datasources")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var legacyTenantAdmin = await (await ClientAsync("S-ADM", "default-tenant", "GovernanceAdmin")).GetAsync("/api/v1/catalog/datasources");
        legacyTenantAdmin.StatusCode.ShouldBe(HttpStatusCode.OK);
        var legacyBody = await BodyAsync(legacyTenantAdmin);
        legacyBody.ShouldNotContain("dom_a");
        legacyBody.ShouldNotContain("dom_b");

        var tenantAdmin = await BodyAsync(await (await ClientAsync("S-B-ADM", TenantB, "GovernanceAdmin")).GetAsync("/api/v1/catalog/datasources"));
        tenantAdmin.ShouldContain("dom_b");
        tenantAdmin.ShouldNotContain("dom_a");

        var cluster = await BodyAsync(await (await ClientAsync("S-ROOT", TenantA, "ClusterAdmin")).GetAsync("/api/v1/catalog/datasources"));
        cluster.ShouldContain("dom_a");
        cluster.ShouldContain("dom_b");
    }

    [Fact]
    public async Task TenantBGovernanceAdmin_Multi_RestAndMcp_ShowNoTenantATable()
    {
        var client = await ClientAsync("S-B-ADM", TenantB, "GovernanceAdmin");

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("invoices_b");
        list.ShouldNotContain("orders_a");
        (await client.GetAsync("/api/v1/catalog/datasets/dom_a.main.orders_a")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (listError, mcpList) = await McpTestClient.CallJsonAsync(mcp, "list_datasets", new { });
        listError.ShouldBeFalse(mcpList.ToString());
        mcpList.GetProperty("datasets").EnumerateArray().Select(d => d.GetProperty("id").GetString()).ShouldBe(["dom_b.main.invoices_b"]);
    }

    [Fact]
    public async Task McpTools_ListDatasources_AdminOnly_SearchCatalogFiltered()
    {
        var user = await ClientAsync("S-A-USER", TenantA, "Reader");
        await using (var mcp = await McpTestClient.ConnectAsync(user))
        {
            var (_, forbidden) = await McpTestClient.CallAsync(mcp, "list_datasources", new { });
            forbidden.ShouldContain("Forbidden");

            var (_, search) = await McpTestClient.CallAsync(mcp, "search_catalog", new { query = "invoices" });
            search.ShouldNotContain("invoices_b");
        }

        var cluster = await ClientAsync("S-ROOT", TenantA, "ClusterAdmin");
        await using var adminMcp = await McpTestClient.ConnectAsync(cluster);
        var (_, sources) = await McpTestClient.CallAsync(adminMcp, "list_datasources", new { });
        sources.ShouldContain("dom_a");
        sources.ShouldContain("dom_b");
    }

    [Fact]
    public async Task DefaultTenant_GovernanceAdminWithoutTenantClaim_SeesTheWholeCatalog()
    {
        using var single = Configure(_fixture.Root, "Single");
        await SeedAsync(single);
        var client = await ClientAsync("S-ADM", null, "GovernanceAdmin", single);
        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("orders_a");
        list.ShouldContain("invoices_b");
        (await client.GetAsync("/api/v1/catalog/datasources")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Issue #111 (F-C7): tier fixtures seeded through the repository, one leak matrix per channel ----

    private static readonly TableIdentifier SummaryT = new("dom_s", "main", "sum_t");
    private static readonly TableIdentifier FullT = new("dom_s", "main", "full_t");
    private static readonly TableIdentifier HiddenT = new("dom_s", "main", "hid_t");
    private static readonly TableIdentifier UnsharedFullT = new("dom_s", "main", "unshared_full_t");

    private static async Task SeedTiersAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var admin = new Sid("S-ADMIN");
        foreach (var (id, tier) in new[]
                 {
                     (SummaryT, DatasetVisibility.Summary), (FullT, DatasetVisibility.Full),
                     (HiddenT, DatasetVisibility.Hidden), (UnsharedFullT, DatasetVisibility.Full)
                 })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = id.Domain, Description = "Tier fixture " + id.TableName },
                Identifier = id,
                Columns = [new() { ColumnName = "id", DataType = "integer" }, new() { ColumnName = "tier_secret_col", DataType = "varchar" }]
            });
            await repo.SetDatasetVisibilityAsync(id, tier, admin);
        }

        await repo.ChangeDatasetTagsAsync(SummaryT, ["finance"], [], admin);
    }

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient User)> TierFactoryAsync(string mode = "Multi", string sid = "S-T-USER", string? tenant = TenantB, string roles = "Reader")
    {
        if (mode == "Multi")
        {
            if (!_fixture.TierSeeded)
            {
                await SeedTiersAsync(_fixture.TierFactory);
                _fixture.TierSeeded = true;
            }

            return (_fixture.TierFactory, await ClientAsync(sid, tenant, roles, _fixture.TierFactory));
        }

        var factory = Configure(_fixture.Root, mode, SummaryT.ToString().ToLowerInvariant(), FullT.ToString().ToLowerInvariant(), HiddenT.ToString().ToLowerInvariant());
        await SeedTiersAsync(factory);
        return (factory, await ClientAsync(sid, tenant, roles, factory));
    }

    // Authorized discovery (OD-19): the persisted tier is ignored; an in-scope table (shared, owned by the caller's tenant or consented
    // to it) is listed for every authenticated caller and a column the caller may not read carries the state Deny. A table outside the
    // caller's tenant scope is absent and answers exactly like a nonexistent dataset.
    [Fact]
    public async Task Rest_Discovery_SharedTablesAreListedWithDeniedColumns_UnsharedTableIsOutOfScope()
    {
        var (_, client) = await TierFactoryAsync();

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("sum_t");
        list.ShouldContain("full_t");
        list.ShouldContain("hid_t");
        list.ShouldNotContain("unshared_full_t");

        foreach (var shared in new[] { "sum_t", "full_t", "hid_t" })
        {
            var detail = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main." + shared);
            detail.StatusCode.ShouldBe(HttpStatusCode.OK);
            MaskingStatesOf(await BodyAsync(detail)).Values.ShouldAllBe(state => state == "Deny");
        }

        var unshared = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.unshared_full_t");
        var missing = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.nothing_t");
        unshared.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unshared.StatusCode.ShouldBe(missing.StatusCode);
        // Same problem document; only the per-request identifiers (instance path, correlation and trace id) differ.
        WithoutRequestIdentity(await BodyAsync(unshared)).ShouldBe(WithoutRequestIdentity(await BodyAsync(missing)));
    }

    [Fact]
    public async Task Rest_Discovery_ConsentedColumnIsClear_EveryOtherColumnStaysDeny_ForTheConsentedCallerOnly()
    {
        var (factory, client) = await TierFactoryAsync(sid: "S-T-CONSENTED");
        using (var scope = factory.Services.CreateScope())
        {
            await ConsentAsync(scope.ServiceProvider.GetRequiredService<IGovernanceRepository>(), HiddenT, "S-T-CONSENTED", TenantB, "id");
        }

        var granted = MaskingStatesOf(await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t")));
        granted["id"].ShouldBe("Clear");
        granted["tier_secret_col"].ShouldBe("Deny");

        var other = await ClientAsync("S-T-OTHER", TenantB, "Reader", factory);
        MaskingStatesOf(await BodyAsync(await other.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t"))).Values.ShouldAllBe(state => state == "Deny");
    }

    private static Dictionary<string, string> MaskingStatesOf(string detailJson)
    {
        using var doc = JsonDocument.Parse(detailJson);
        return doc.RootElement.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("maskingState").GetString()!);
    }

    [Fact]
    public async Task Admins_SeeEveryTier_Unnarrowed_LegacyIdTenantAdminInMultiIsConfined()
    {
        var (factory, _) = await TierFactoryAsync();
        var cluster = await ClientAsync("S-ROOT", TenantB, "ClusterAdmin", factory);
        var clusterList = await BodyAsync(await cluster.GetAsync("/api/v1/catalog/datasets"));
        clusterList.ShouldContain("hid_t");
        (await BodyAsync(await cluster.GetAsync("/api/v1/catalog/datasets/dom_s.main.sum_t"))).ShouldContain("tier_secret_col");

        // WP-DT-4: a truly tenant-less principal (Multi, no default tenant) is refused at the gate (S6, 403 TENANT_UNRESOLVED) ...
        var tenantless = await ClientAsync("S-ADM", null, "GovernanceAdmin", factory);
        (await tenantless.GetAsync("/api/v1/catalog/datasets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // ... while the legacy id carried as an explicit tenant claim is an ordinary tenant and confined: no unshared table (a shared table
        // shows to a tenant administrator in every tier, as for any tenant).
        var claimless = await ClientAsync("S-ADM", "default-tenant", "GovernanceAdmin", factory);
        var claimlessList = await BodyAsync(await claimless.GetAsync("/api/v1/catalog/datasets"));
        claimlessList.ShouldNotContain("unshared_full_t");
        (await claimless.GetAsync("/api/v1/catalog/datasets/dom_s.main.unshared_full_t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DefaultTenant_EveryTableIsTierEligible_ClaimlessUserSeesSummaryAndFull()
    {
        using var single = Configure(_fixture.Root, "Single");
        await SeedTiersAsync(single);
        var client = await ClientAsync("S-T-USER", null, "Reader", single);

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("unshared_full_t");
        list.ShouldContain("sum_t");
        list.ShouldContain("hid_t");
    }

    [Fact]
    public async Task DataAccess_PersistedSummaryOrFull_WithoutConsent_Dropped_OnTheDataPaths()
    {
        var (factory, _) = await TierFactoryAsync();
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var consents = scope.ServiceProvider.GetRequiredService<IConsentRepository>();
        var tables = await repo.GetAllTablesAsync();
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-T-USER"),
            new System.Security.Claims.Claim("tenant_id", TenantB)
        ], "test"));

        var viaDefault = await Autheris.Application.Services.CatalogVisibility.VisibleTablesAsync(tables, user, new TenantId(TenantB), consents);

        viaDefault.ShouldBeEmpty();
    }
}
