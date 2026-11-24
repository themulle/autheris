namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Container visibility over HTTP: an object of an Open container, or of a container the caller holds a catalog grant on, is
/// listed (metadata only); an object that is not provably visible answers exactly like a missing one (no existence oracle).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogExistenceOracleIntegrationTests : IClassFixture<CatalogExistenceOracleIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string Caller = "S-ORACLE-CALLER";
    private static readonly Sid Owner = new("S-ORACLE-OWNER");
    private static readonly TableIdentifier InOpen = new("dom_oracle", "main", "in_open");
    private static readonly TableIdentifier InClosed = new("dom_oracle", "main", "in_closed");
    private static readonly TableIdentifier InGranted = new("dom_oracle", "main", "in_granted");
    private static readonly TableIdentifier Missing = new("dom_oracle", "main", "does_not_exist");

    private readonly HostFixture _fixture;

    public CatalogExistenceOracleIntegrationTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Status_and_body_identical()
    {
        await SeedAsync();
        var client = Client();

        var invisible = await client.GetAsync($"/api/v1/catalog/objects/{InClosed}");
        var missing = await client.GetAsync($"/api/v1/catalog/objects/{Missing}");

        invisible.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(invisible.StatusCode);
        Shape(await invisible.Content.ReadAsStringAsync()).ShouldBe(Shape(await missing.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Open_container_object_is_visible_without_any_grant()
    {
        await SeedAsync();

        var response = await Client().GetAsync($"/api/v1/catalog/objects/{InOpen}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Catalog_grant_makes_the_container_object_visible()
    {
        await SeedAsync();

        var response = await Client().GetAsync($"/api/v1/catalog/objects/{InGranted}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_caller_without_the_grant_gets_the_missing_answer()
    {
        await SeedAsync();

        var response = await Client("S-ORACLE-OTHER").GetAsync($"/api/v1/catalog/objects/{InGranted}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The comparable part of a problem body: everything except per-request values (trace id, instance).</summary>
    private static string Shape(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var parts = new List<string>();
        foreach (var name in new[] { "type", "title", "status", "detail", "code" })
        {
            parts.Add(doc.RootElement.TryGetProperty(name, out var value) ? $"{name}={value}" : $"{name}=-");
        }

        return string.Join('|', parts);
    }

    private async Task SeedAsync()
    {
        await _fixture.Gate.WaitAsync();
        try
        {
            if (_fixture.Seeded)
            {
                return;
            }

            using var scope = _fixture.Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var domains = scope.ServiceProvider.GetRequiredService<IGovernanceDomainRepository>();
            var grants = scope.ServiceProvider.GetRequiredService<ICatalogVisibilityGrantRepository>();
            var tenant = new TenantId(TenantA);

            var openId = await UpsertAsync(repo, InOpen);
            var closedId = await UpsertAsync(repo, InClosed);
            var grantedId = await UpsertAsync(repo, InGranted);

            await CatalogVisibilitySeeding.SeedOpenDomainAsync(domains, tenant, Owner, openId);
            await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, tenant, Owner, closedId);
            var grantedDomain = await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, tenant, Owner, grantedId);
            await CatalogVisibilitySeeding.SeedCatalogGrantAsync(
                grants, tenant, new ContainerRef(ContainerKind.Domain, grantedDomain.Id), new Sid(Caller), Owner);

            _fixture.Seeded = true;
        }
        finally
        {
            _fixture.Gate.Release();
        }
    }

    private static async Task<Guid> UpsertAsync(IGovernanceRepository repo, TableIdentifier id)
    {
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "PostgreSql", SourceName = id.Domain },
            Identifier = id,
            Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
        });
        var stored = await repo.GetTableMetadataAsync(id);
        return stored!.Table.Id;
    }

    private HttpClient Client(string sid = Caller)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TenantA);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");
        return client;
    }

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public System.Threading.SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Seeded { get; set; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("CatalogExistenceOracle"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            Gate.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
