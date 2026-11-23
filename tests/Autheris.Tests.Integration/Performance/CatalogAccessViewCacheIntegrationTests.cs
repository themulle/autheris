namespace Autheris.Tests.Integration.Performance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1044 on PostgreSQL through the REST API: the caller's access view is cached across requests, and every governance change
/// between two requests is visible in the second one (list and facets with <c>accessStatus=granted</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogAccessViewCacheIntegrationTests : IClassFixture<CatalogAccessViewCacheIntegrationTests.HostFixture>
{
    private const string Tenant = "tenant-view";
    private const string UserSid = "S-1-5-21-1044-1044-1044-1001";
    private const string GroupSid = "S-1-5-21-1044-1044-1044-3001";

    private readonly HostFixture _fixture;

    public CatalogAccessViewCacheIntegrationTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RevokeBetweenTwoRequests_RemovesTheTableFromListAndFacets()
    {
        var schema = await _fixture.SeedSchemaAsync("revoke", tables: 4);
        var grant = await _fixture.GrantAsync(schema, "t1");
        await _fixture.GrantAsync(schema, "t2");
        (await GrantedAsync(schema)).ShouldBe(["t1", "t2"]);

        (await FacetCountAsync(schema)).ShouldBe(2);

        await _fixture.RevokeAsSelfAsync(grant.Id);

        // Facets first: they have no page guard, so only the invalidation can drop the table.
        (await FacetCountAsync(schema)).ShouldBe(1);
        (await GrantedAsync(schema)).ShouldBe(["t2"]);
        (await GrantedCountAsync(schema)).ShouldBe(1);
    }

    [Fact]
    public async Task NewGrantBetweenTwoRequests_AddsTheTable()
    {
        var schema = await _fixture.SeedSchemaAsync("grant", tables: 4);
        await _fixture.GrantAsync(schema, "t1");
        (await GrantedAsync(schema)).ShouldBe(["t1"]);

        await _fixture.GrantAsync(schema, "t3");

        (await GrantedAsync(schema)).ShouldBe(["t1", "t3"]);
    }

    [Fact]
    public async Task NewTableDenyBetweenTwoRequests_RemovesTheTable()
    {
        var schema = await _fixture.SeedSchemaAsync("deny", tables: 3);
        await _fixture.GrantAsync(schema, "t1");
        await _fixture.GrantAsync(schema, "t2");
        (await GrantedAsync(schema)).ShouldBe(["t1", "t2"]);

        (await FacetCountAsync(schema)).ShouldBe(2);

        await _fixture.GrantAsync(schema, "t2", ConsentEffect.Deny);

        (await FacetCountAsync(schema)).ShouldBe(1);
        (await GrantedAsync(schema)).ShouldBe(["t1"]);
    }

    [Fact]
    public async Task PatternDenyCreatedAndRevokedBetweenRequests_IsVisibleEachTime()
    {
        var schema = await _fixture.SeedSchemaAsync("pattern", tables: 3);
        await _fixture.GrantAsync(schema, "t1");
        await _fixture.GrantAsync(schema, "t2");
        (await GrantedAsync(schema)).ShouldBe(["t1", "t2"]);

        (await FacetCountAsync(schema)).ShouldBe(2);

        var deny = await _fixture.PatternDenyAsync(schema);
        (await FacetCountAsync(schema)).ShouldBe(0);
        (await GrantedAsync(schema)).ShouldBeEmpty();

        await _fixture.RevokeAsSelfAsync(deny.Id);
        (await GrantedAsync(schema)).ShouldBe(["t1", "t2"]);
    }

    [Fact]
    public async Task TableDeactivatedBetweenTwoRequests_DisappearsFromTheGrantedSet()
    {
        var schema = await _fixture.SeedSchemaAsync("catalog", tables: 3);
        await _fixture.GrantAsync(schema, "t1");
        await _fixture.GrantAsync(schema, "t2");
        (await GrantedAsync(schema)).ShouldBe(["t1", "t2"]);

        await _fixture.DeactivateAsync(schema, "t2");

        (await GrantedAsync(schema)).ShouldBe(["t1"]);
    }

    private async Task<List<string>> GrantedAsync(string schema)
    {
        using var client = Client();
        var response = await client.GetAsync($"/api/v1/catalog/objects?pageSize=200&accessStatus=granted&schema={schema}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(static i => i.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The granted count of <paramref name="schema"/> from the facets endpoint (no page guard behind it).</summary>
    private async Task<int> FacetCountAsync(string schema)
    {
        using var client = Client();
        var response = await client.GetAsync("/api/v1/catalog/objects/facets?accessStatus=granted");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("domains").EnumerateArray()
            .Where(static d => d.GetProperty("domain").GetString() == "dom_view")
            .SelectMany(static d => d.GetProperty("schemas").EnumerateArray())
            .Where(sc => string.Equals(sc.GetProperty("schema").GetString(), schema, StringComparison.OrdinalIgnoreCase))
            .Sum(static sc => sc.GetProperty("count").GetInt32());
    }

    private async Task<int> GrantedCountAsync(string schema)
    {
        using var client = Client();
        var response = await client.GetAsync($"/api/v1/catalog/objects?pageSize=200&accessStatus=granted&schema={schema}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("totalCount").GetInt32();
    }

    private HttpClient Client()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", UserSid);
        client.DefaultRequestHeaders.Add("X-Test-Group-Sids", GroupSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");
        return client;
    }

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();

        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
                b.UseSetting("Gateway:Catalog:Discovery:RequestsPerMinute", "100000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogAccessViewCache"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            });
        }

        private IGovernanceRepository Repo() => Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IGovernanceRepository>();

        public async Task<string> SeedSchemaAsync(string name, int tables)
        {
            var schema = $"{name}_{Guid.NewGuid():N}"[..20];
            var repo = Repo();
            for (var i = 1; i <= tables; i++)
            {
                var table = new Table { Id = Guid.NewGuid(), DisplayName = $"t{i}", TableName = $"t{i}", SchemaName = schema, SourceType = "PostgreSql", SourceName = "dom_view", IsActive = true };
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = table,
                    Identifier = new TableIdentifier("dom_view", schema, $"t{i}"),
                    Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "c00", DataType = "text" }]
                });
            }

            return schema;
        }

        public async Task<Consent> GrantAsync(string schema, string table, ConsentEffect effect = ConsentEffect.Allow)
        {
            var repo = Repo();
            var id = new TableIdentifier("dom_view", schema, table);
            var meta = await repo.GetTableMetadataAsync(id);
            return await repo.CreateConsentAsync(new Consent
            {
                TableId = meta!.Table.Id,
                TableIdentifier = id,
                TenantId = new TenantId(Tenant),
                Effect = effect,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(UserSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = effect == ConsentEffect.Allow ? [new ConsentColumnRule { ColumnName = "c00", AccessLevel = ColumnAccessLevel.Clear }] : []
            });
        }

        public Task<Consent> PatternDenyAsync(string schema) => Repo().CreateConsentAsync(new Consent
        {
            TableId = Guid.Empty,
            ObjectPattern = $"dom_view.{schema}.*",
            DataSource = "dom_view",
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            WorkflowMode = ConsentWorkflowMode.External,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        });

        public Task RevokeAsSelfAsync(Guid consentId)
        {
            // The grantee revokes its own consent.
            var self = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.PrimarySid, UserSid), new Claim("tenant_id", Tenant)], "test"));
            return Repo().RevokeConsentAsync(consentId, new Sid(UserSid), "test revoke", ConsentRevocationAuthority.FromPrincipal(self));
        }

        public async Task DeactivateAsync(string schema, string table)
        {
            var repo = Repo();
            var meta = await repo.GetTableMetadataAsync(new TableIdentifier("dom_view", schema, table));
            await repo.UpsertTableMetadataAsync(meta! with { Table = meta.Table with { IsActive = false } });
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
