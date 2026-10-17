namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    private static WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, string tenancyMode, bool rebac = false) =>
        factory.WithWebHostBuilder(b =>
        {
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
    public async Task Datasources_AdminOnly_NonAdmin403_ClaimlessAdmin403WithCode_TenantAdminConfined_ClusterAdminSeesAll()
    {
        (await (await ClientAsync("S-A-USER", TenantA, "Reader")).GetAsync("/api/v1/catalog/datasources")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var claimless = await (await ClientAsync("S-ADM", null, "GovernanceAdmin")).GetAsync("/api/v1/catalog/datasources");
        claimless.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyAsync(claimless)).ShouldContain("TENANCY_MODE_CLAIMLESS_ADMIN");

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
}
