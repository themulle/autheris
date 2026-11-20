namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogFacetsSecurityIntegrationTests : IClassFixture<CatalogFacetsSecurityIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly TableIdentifier OrdersA = new("dom_a", "main", "orders_facets");
    private static readonly TableIdentifier InvoicesB = new("dom_b", "main", "invoices_facets");

    private readonly HostFixture _fixture;

    public CatalogFacetsSecurityIntegrationTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Facets_Anonymous_Is401()
    {
        var response = await _fixture.Factory.CreateClient().GetAsync("/api/v1/catalog/objects/facets");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Facets_ReadOnlyToken_Allowed()
    {
        await SeedAsync();
        var client = Client("S-FACET-A", TenantA, "Autheris.Catalog.Read ReadOnlyTest");

        var blockedWrite = await client.PostAsync("/api/v1/governance/consents/bundle", new StringContent("{}"));
        blockedWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await blockedWrite.Content.ReadAsStringAsync()).ShouldContain("READ_ONLY_TOKEN");

        var response = await client.GetAsync("/api/v1/catalog/objects/facets");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Facets_ForeignTenantObjectsNeverCounted()
    {
        await SeedAsync();
        var client = Client("S-FACET-A", TenantA);

        var response = await client.GetAsync("/api/v1/catalog/objects/facets");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("dom_b");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var domainNames = doc.RootElement.GetProperty("domains").EnumerateArray().Select(d => d.GetProperty("domain").GetString()!).ToList();
        domainNames.ShouldBe(["dom_a"]);
    }

    [Fact]
    public async Task Facets_RouteNotShadowedByObjectId()
    {
        await SeedAsync();
        var client = Client("S-FACET-A", TenantA);

        var facetsResponse = await client.GetAsync("/api/v1/catalog/objects/facets");
        facetsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await facetsResponse.Content.ReadAsStringAsync()))
        {
            doc.RootElement.TryGetProperty("domains", out _).ShouldBeTrue();
        }

        var detailResponse = await client.GetAsync($"/api/v1/catalog/objects/{OrdersA}");
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("objectId").GetString().ShouldBe(OrdersA.ToString());
        }
    }

    [Fact]
    public async Task Facets_RateLimited()
    {
        var client = _fixture.RateLimitedFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-FACET-LIMIT");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TenantA);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? throttled = null;
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.GetAsync("/api/v1/catalog/objects/facets");
            statuses.Add(resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled = resp;
                break;
            }
        }

        throttled.ShouldNotBeNull($"Expected 429 TooManyRequests within burst, saw: {string.Join(",", statuses)}");
        throttled!.Headers.RetryAfter.ShouldNotBeNull();
    }

    private async Task SeedAsync()
    {
        if (_fixture.Seeded)
        {
            return;
        }

        using var scope = _fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = OrdersA.TableName, TableName = OrdersA.TableName, SchemaName = OrdersA.Schema, SourceType = "PostgreSql", SourceName = OrdersA.Domain },
            Identifier = OrdersA,
            Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
        });
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = InvoicesB.TableName, TableName = InvoicesB.TableName, SchemaName = InvoicesB.Schema, SourceType = "PostgreSql", SourceName = InvoicesB.Domain },
            Identifier = InvoicesB,
            Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
        });
        var metaA = await repo.GetTableMetadataAsync(OrdersA);
        var metaB = await repo.GetTableMetadataAsync(InvoicesB);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = metaA!.Table.Id,
            TableIdentifier = OrdersA,
            TenantId = new TenantId(TenantA),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-FACET-A"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });
        await repo.CreateConsentAsync(new Consent
        {
            TableId = metaB!.Table.Id,
            TableIdentifier = InvoicesB,
            TenantId = new TenantId(TenantB),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-FACET-B"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });
        _fixture.Seeded = true;
    }

    private HttpClient Client(string sid, string tenant, string? scopes = "Autheris.Catalog.Read")
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        if (scopes is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scopes);
        }

        return client;
    }

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public WebApplicationFactory<Program> RateLimitedFactory { get; }
        public bool Seeded { get; set; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogFacetsSecurity"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
                b.ConfigureServices(services => services.AddTransient<IClaimsTransformation, ReadOnlyTestClaimsTransformation>());
            });

            RateLimitedFactory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:Catalog:Discovery:RequestsPerMinute", "1");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogFacetsRateLimit"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            });
        }

        public void Dispose()
        {
            RateLimitedFactory.Dispose();
            Factory.Dispose();
            Root.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private sealed class ReadOnlyTestClaimsTransformation(
        IIdentityPipeline pipeline,
        IHttpContextAccessor? httpContextAccessor = null) : IClaimsTransformation
    {
        private readonly EnterpriseClaimsTransformation _inner = new(pipeline, httpContextAccessor);

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var transformed = await _inner.TransformAsync(principal).ConfigureAwait(false);
            if (transformed.HasScope("ReadOnlyTest") && transformed.Identity is ClaimsIdentity identity)
            {
                TokenAccessScope.MarkReadOnly(identity);
            }

            return transformed;
        }
    }
}
