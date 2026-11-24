namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
/// #1063 §1: End-to-end parity across <c>/api/v1/catalog/objects</c>, <c>/datasets</c>, <c>/search</c>,
/// <c>/objects/facets</c>, <c>/objects/{id}</c>, and <c>/datasets/{id}</c> through the shared catalog visibility evaluator.
/// </summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogSurfaceParityIntegrationTests : IClassFixture<ExternalCatalogSurfaceParityIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserSid = "S-1-PARITY-INT-USER";
    private static readonly TableIdentifier OpenTable = new("dom_parity", "main", "open_orders");
    private static readonly TableIdentifier OnRequestTable = new("dom_parity", "main", "restricted_orders");
    private static readonly TableIdentifier GrantedTable = new("dom_parity", "main", "granted_orders");
    private static readonly TableIdentifier HiddenOwnedTable = new("dom_parity", "main", "hidden_owned_orders");
    private static readonly TableIdentifier HiddenSecretTable = new("dom_parity", "main", "hidden_secret_orders");
    private static readonly TableIdentifier ForeignTable = new("dom_b", "main", "foreign_orders");

    public sealed class HostFixture : IDisposable
    {
        public WebApplicationFactory<Program> Root { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public bool Seeded { get; set; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("SurfaceParity"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Tenancy:DefaultTenantId"] = null
                }));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private readonly HostFixture _fixture;

    public ExternalCatalogSurfaceParityIntegrationTests(HostFixture fixture) => _fixture = fixture;

    private async Task EnsureSeededAsync()
    {
        if (_fixture.Seeded)
        {
            return;
        }

        using var scope = _fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, hidden, ownerTenant) in new (TableIdentifier Id, bool Hidden, string? OwnerTenant)[]
        {
            (OpenTable, false, TenantA),
            (OnRequestTable, false, TenantA),
            (GrantedTable, false, TenantA),
            (HiddenOwnedTable, true, TenantA),
            (HiddenSecretTable, true, TenantA),
            (ForeignTable, false, TenantB)
        })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = id.TableName,
                    TableName = id.TableName,
                    SchemaName = id.Schema,
                    SourceType = "PostgreSql",
                    SourceName = id.Domain,
                    DiscoveryHidden = hidden,
                    IsActive = true
                },
                Identifier = id,
                OwnerTenantId = ownerTenant,
                Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
            });
        }

        await OpenDomainSeeder.AssignToOpenDomainAsync(_fixture.Factory.Services, new TenantId(TenantA), OpenTable);

        var grantedMeta = await repo.GetTableMetadataAsync(GrantedTable);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = grantedMeta!.Table.Id,
            TableIdentifier = GrantedTable,
            TenantId = new TenantId(TenantA),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });

        var hiddenOwnedMeta = await repo.GetTableMetadataAsync(HiddenOwnedTable);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = hiddenOwnedMeta!.Table.Id,
            TableIdentifier = HiddenOwnedTable,
            TenantId = new TenantId(TenantA),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });

        // Container ownership (#1009): hidden_owned_orders lives in a container whose owner group is the caller.
        var domains = scope.ServiceProvider.GetRequiredService<IGovernanceDomainRepository>();
        await CatalogVisibilitySeeding.SeedOnRequestDomainAsync(domains, new TenantId(TenantA), new Sid(UserSid), hiddenOwnedMeta.Table.Id);

        _fixture.Seeded = true;
    }

    private HttpClient CreateUserClient()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", UserSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TenantA);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");
        return client;
    }

    [Fact]
    public async Task ExternalCatalogSurfaces_MatchAcrossListDatasetsSearchFacetsAndDetail()
    {
        await EnsureSeededAsync();
        using var client = CreateUserClient();

        string[] expected =
        [
            GrantedTable.ToString(),
            HiddenOwnedTable.ToString(),
            OpenTable.ToString()
        ];

        using var objResp = await client.GetAsync("/api/v1/catalog/objects");
        objResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var objDoc = JsonDocument.Parse(await objResp.Content.ReadAsStringAsync());
        var listIds = objDoc.RootElement.GetProperty("items").EnumerateArray()
            .Select(static e => e.GetProperty("objectId").GetString()!)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        listIds.ShouldBe(expected);

        using var datasetResp = await client.GetAsync("/api/v1/catalog/datasets");
        datasetResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dsDoc = JsonDocument.Parse(await datasetResp.Content.ReadAsStringAsync());
        var dsIds = dsDoc.RootElement.EnumerateArray()
            .Select(static e => e.GetProperty("datasetId").GetString()!)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        dsIds.ShouldBe(expected);

        using var searchResp = await client.GetAsync("/api/v1/catalog/search?q=orders&mode=keyword");
        searchResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchIds = searchDoc.RootElement.EnumerateArray()
            .Select(ExtractSearchId)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        searchIds.ShouldBe(expected);

        using var facetResp = await client.GetAsync("/api/v1/catalog/objects/facets");
        facetResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var facetDoc = JsonDocument.Parse(await facetResp.Content.ReadAsStringAsync());
        facetDoc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(expected.Length);

        foreach (var id in expected)
        {
            (await client.GetAsync($"/api/v1/catalog/objects/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync($"/api/v1/catalog/datasets/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach (var hiddenOrOutOfScope in new[] { OnRequestTable, HiddenSecretTable, ForeignTable })
        {
            (await client.GetAsync($"/api/v1/catalog/objects/{hiddenOrOutOfScope}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync($"/api/v1/catalog/datasets/{hiddenOrOutOfScope}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    private static string ExtractSearchId(JsonElement entry)
    {
        if (entry.TryGetProperty("tableIdentifier", out var tid))
        {
            if (tid.ValueKind == JsonValueKind.String)
            {
                return tid.GetString()!;
            }

            if (tid.ValueKind == JsonValueKind.Object)
            {
                return $"{tid.GetProperty("domain").GetString()}.{tid.GetProperty("schema").GetString()}.{tid.GetProperty("tableName").GetString()}";
            }
        }

        return entry.GetProperty("datasetId").GetString()!;
    }
}
