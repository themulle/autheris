namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// WP-0 (PLAN-GOV-111 M-12, T-W0-5, T-W0-6): the REST catalog and the MCP tools show a consent-less caller nothing, a consented caller only the
/// consented columns, and a tenant-B GovernanceAdmin nothing of tenant A, over the real pipeline (authentication, authorization policies, DI) on SQLite.
/// The test authentication handler issues a tenant claim for every request (the legacy single tenant when no tenant header is sent), so
/// a request without <c>X-Test-Tenant</c> is a principal without a real tenant claim.
/// </summary>
public sealed class CatalogChannelParityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly TableIdentifier OrdersA = new("dom_a", "main", "orders_a");
    private static readonly TableIdentifier InvoicesB = new("dom_b", "main", "invoices_b");

    private readonly WebApplicationFactory<Program> _factory;
    private bool _seeded;

    public CatalogChannelParityTests(WebApplicationFactory<Program> factory)
    {
        _factory = Configure(factory, "Multi");
    }

    private static WebApplicationFactory<Program> Configure(
        WebApplicationFactory<Program> factory, string tenancyMode, bool rebac = false, params string[] sharedTables) =>
        factory.WithWebHostBuilder(b =>
        {
            for (var i = 0; i < sharedTables.Length; i++)
            {
                b.UseSetting($"Gateway:Catalog:SharedTables:{i}", sharedTables[i]);
            }

            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-CatalogParity-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            b.UseSetting("Gateway:Tenancy:Mode", tenancyMode);
            b.UseSetting("Gateway:Rebac:Enabled", rebac ? "true" : "false");
        });

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        foreach (var (id, description) in new[] { (OrdersA, "Orders of tenant A"), (InvoicesB, "Invoices of tenant B") })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "Sqlite", SourceName = id.Domain, Description = description },
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
        if (!_seeded && factory == null)
        {
            await SeedAsync(_factory);
            _seeded = true;
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

    [Fact]
    public async Task Rest_ConsentlessCaller_SeesNothing_AndDetailIsA404()
    {
        var client = await ClientAsync("S-B-USER", TenantB, "Reader");

        var list = await client.GetAsync("/api/v1/catalog/datasets");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyAsync(list)).ShouldBe("[]");
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
        detail.ShouldContain("amount");
        detail.ShouldNotContain("secret_note");

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
        var legacyTenantAdmin = await (await ClientAsync("S-ADM", "legacy-single-tenant", "GovernanceAdmin")).GetAsync("/api/v1/catalog/datasources");
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

    // ReBAC stays an additional filter: with ReBAC enabled and no tuples a consented caller sees nothing (intersection, never a union).
    [Fact]
    public async Task ReBacEnabledWithoutTuples_ConsentedCallerSeesNothing()
    {
        var rebac = Configure(new WebApplicationFactory<Program>(), "Multi", rebac: true);
        await SeedAsync(rebac);
        var client = await ClientAsync("S-A-USER", TenantA, "Reader", rebac);

        (await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"))).ShouldBe("[]");
    }

    [Fact]
    public async Task SingleMode_GovernanceAdminWithoutTenantClaim_IsTheUnrestrictedAdministrator()
    {
        var single = Configure(new WebApplicationFactory<Program>(), "Single");
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
                Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "Sqlite", SourceName = id.Domain, Description = "Tier fixture " + id.TableName },
                Identifier = id,
                Columns = [new() { ColumnName = "id", DataType = "integer" }, new() { ColumnName = "tier_secret_col", DataType = "varchar" }]
            });
            await repo.SetDatasetVisibilityAsync(id, tier, admin);
        }

        await repo.ChangeDatasetTagsAsync(SummaryT, ["finance"], [], admin);
    }

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient User)> TierFactoryAsync(string mode = "Multi", string sid = "S-T-USER", string? tenant = TenantB, string roles = "Reader")
    {
        var factory = Configure(new WebApplicationFactory<Program>(), mode, rebac: false, SummaryT.ToString().ToLowerInvariant(), FullT.ToString().ToLowerInvariant(), HiddenT.ToString().ToLowerInvariant());
        await SeedTiersAsync(factory);
        return (factory, await ClientAsync(sid, tenant, roles, factory));
    }

    [Fact]
    public async Task Rest_TierMatrix_SummaryNoColumns_FullColumns_HiddenAndUnsharedAbsent()
    {
        var (_, client) = await TierFactoryAsync();

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("sum_t");
        list.ShouldContain("full_t");
        list.ShouldNotContain("hid_t");
        list.ShouldNotContain("unshared_full_t");

        var summary = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.sum_t");
        summary.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyAsync(summary)).ShouldNotContain("tier_secret_col");
        (await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.full_t"))).ShouldContain("tier_secret_col");
        (await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.unshared_full_t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BodyAsync(await client.GetAsync("/api/v1/catalog/search?q=tier_secret_col"))).ShouldNotContain("sum_t");
    }

    [Fact]
    public async Task Mcp_TierMatrix_SummaryAllowListed_FullColumns_HiddenIsNotFound_TagsFromCatalogTags()
    {
        var (_, client) = await TierFactoryAsync();
        await using var mcp = await McpTestClient.ConnectAsync(client);

        var (listError, list) = await McpTestClient.CallJsonAsync(mcp, "list_datasets", new { });
        listError.ShouldBeFalse(list.ToString());
        var rows = list.GetProperty("datasets").EnumerateArray().ToList();
        rows.Select(d => d.GetProperty("id").GetString()).Order().ShouldBe(["dom_s.main.full_t", "dom_s.main.sum_t"]);
        var summaryRow = rows.Single(d => d.GetProperty("id").GetString() == "dom_s.main.sum_t");
        summaryRow.GetProperty("columnCount").GetInt32().ShouldBe(0);
        summaryRow.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["finance"]);

        var (_, summaryText) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.sum_t" });
        summaryText.ShouldContain("summary");
        summaryText.ShouldNotContain("tier_secret_col");
        var (_, fullText) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.full_t" });
        fullText.ShouldContain("tier_secret_col");
        var (hiddenError, _) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.hid_t" });
        hiddenError.ShouldBeTrue();
        var (unsharedError, _) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.unshared_full_t" });
        unsharedError.ShouldBeTrue();
    }

    [Fact]
    public async Task GraphQl_TierMatrix_SummaryNoColumns_FullColumns_HiddenAbsent()
    {
        var (_, client) = await TierFactoryAsync();

        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var response = await client.PostAsJsonAsync("/graphql", new { query = "{ catalog(first: 3) { tableName columns } }" });
        var text = await BodyAsync(response);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var doc = JsonDocument.Parse(text);
        var rows = doc.RootElement.GetProperty("data").GetProperty("catalog").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("tableName").GetString()).Order().ShouldBe(["full_t", "sum_t"]);
        rows.Single(r => r.GetProperty("tableName").GetString() == "sum_t").GetProperty("columns").GetArrayLength().ShouldBe(0);
        rows.Single(r => r.GetProperty("tableName").GetString() == "full_t").GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldContain("tier_secret_col");
        text.ShouldNotContain("hid_t");
    }

    [Fact]
    public async Task HiddenTier_IsIndistinguishableFromANonexistentDataset_OnEveryChannel()
    {
        var (_, client) = await TierFactoryAsync();

        // REST: status and body are identical.
        var hidden = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t");
        var missing = await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.nothing_t");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        hidden.StatusCode.ShouldBe(missing.StatusCode);
        (await BodyAsync(hidden)).ShouldBe(await BodyAsync(missing));

        // MCP: the same tool error, apart from the echoed identifier.
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (hiddenError, hiddenText) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.hid_t" });
        var (missingError, missingText) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.nothing_t" });
        hiddenError.ShouldBeTrue();
        missingError.ShouldBeTrue();
        hiddenText.Replace("hid_t", "x").ShouldBe(missingText.Replace("nothing_t", "x"));

        // GraphQL: a catalog search for either name answers the same empty page.
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var gqlHidden = await BodyAsync(await client.PostAsJsonAsync("/graphql", new { query = "{ catalog(search: \"hid_t\", first: 3) { tableName } }" }));
        var gqlMissing = await BodyAsync(await client.PostAsJsonAsync("/graphql", new { query = "{ catalog(search: \"nothing_t\", first: 3) { tableName } }" }));
        // The cost extension carries a rate-limit counter; the data element is what must be identical.
        JsonDocument.Parse(gqlHidden).RootElement.GetProperty("data").GetRawText().ShouldBe(JsonDocument.Parse(gqlMissing).RootElement.GetProperty("data").GetRawText());
        gqlHidden.ShouldNotContain("hid_t");
    }

    [Fact]
    public async Task ConsentedNonAdmin_OnAHiddenTier_GetsTheGrantedView_OnTheDiscoverySurface()
    {
        var (factory, client) = await TierFactoryAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await ConsentAsync(scope.ServiceProvider.GetRequiredService<IGovernanceRepository>(), HiddenT, "S-T-USER", TenantB, "id");
        }

        // Granted view: the consented column only, never the whole table, and never for an unconsented caller.
        var detail = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t"));
        detail.ShouldContain("\"id\"");
        detail.ShouldNotContain("tier_secret_col");

        await using var mcp = await McpTestClient.ConnectAsync(client);
        var (error, text) = await McpTestClient.CallAsync(mcp, "describe_dataset", new { dataset = "dom_s.main.hid_t" });
        error.ShouldBeFalse(text);
        text.ShouldNotContain("tier_secret_col");

        var other = await ClientAsync("S-T-OTHER", TenantB, "Reader", factory);
        (await other.GetAsync("/api/v1/catalog/datasets/dom_s.main.hid_t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
        var claimless = await ClientAsync("S-ADM", "legacy-single-tenant", "GovernanceAdmin", factory);
        var claimlessList = await BodyAsync(await claimless.GetAsync("/api/v1/catalog/datasets"));
        claimlessList.ShouldNotContain("unshared_full_t");
        (await claimless.GetAsync("/api/v1/catalog/datasets/dom_s.main.unshared_full_t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SingleMode_EveryTableIsTierEligible_ClaimlessUserSeesSummaryAndFull()
    {
        var single = Configure(new WebApplicationFactory<Program>(), "Single");
        await SeedTiersAsync(single);
        var client = await ClientAsync("S-T-USER", null, "Reader", single);

        var list = await BodyAsync(await client.GetAsync("/api/v1/catalog/datasets"));
        list.ShouldContain("unshared_full_t");
        list.ShouldContain("sum_t");
        list.ShouldNotContain("hid_t");
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
