namespace Autheris.Tests.Integration;

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
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #858: <c>callerState.rowScopeDescription</c> over the real pipeline. A caller sees only the description of a consent that applies to
/// the caller in the caller's own tenant; a user grant wins over a group grant.
/// </summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogRowScopeDescriptionIntegrationTests : IClassFixture<ExternalCatalogRowScopeDescriptionIntegrationTests.HostFixture>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string GroupSid = "S-1-5-21-111-222-333-2001";
    private static readonly TableIdentifier Orders = new("dom_a", "main", "orders_desc");

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
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("RowScopeDescription"));
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
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

    public ExternalCatalogRowScopeDescriptionIntegrationTests(HostFixture fixture) => _fixture = fixture;

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
            Table = new Table { Id = Guid.NewGuid(), DisplayName = Orders.TableName, TableName = Orders.TableName, SchemaName = Orders.Schema, SourceType = "PostgreSql", SourceName = Orders.Domain },
            Identifier = Orders,
            Columns = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } }
        });
        var meta = await repo.GetTableMetadataAsync(Orders);
        await GrantAsync(repo, meta!.Table.Id, TenantA, GranteeType.User, "S-A-USER", "Your own user grant text.");
        await GrantAsync(repo, meta.Table.Id, TenantA, GranteeType.Group, GroupSid, "Group grant text.");
        await GrantAsync(repo, meta.Table.Id, TenantA, GranteeType.User, "S-A-GROUPONLY-OTHER", "Someone else's text.");
        await Autheris.Tests.Integration.Governance.OpenDomainSeeder.AssignToOpenDomainAsync(
            _fixture.Factory.Services, new TenantId(TenantA), Orders);
        _fixture.Seeded = true;
    }

    private static Task GrantAsync(IGovernanceRepository repo, Guid tableId, string tenant, GranteeType type, string sid, string description) =>
        repo.CreateConsentAsync(new Consent
        {
            TableId = tableId,
            TableIdentifier = Orders,
            TenantId = new TenantId(tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = type,
            GranteeSid = new Sid(sid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Description = description,
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });

    private async Task<JsonElement> CallerStateAsync(string sid, string tenant, string? groupSids = null)
    {
        await SeedAsync();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");
        if (groupSids is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Group-Sids", groupSids);
        }

        var response = await client.GetAsync($"/api/v1/catalog/objects/{Orders}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("callerState").Clone();
    }

    private static string? Description(JsonElement state) =>
        state.TryGetProperty("rowScopeDescription", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    [Fact]
    public async Task ExternalCatalog_RowScopeDescription_TenantIsolation_AcrossTwoTenants()
    {
        // The same grantee in tenant B never sees tenant A's description (and tenant A's table is not in tenant B at all).
        await SeedAsync();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-A-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TenantB);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "Autheris.Catalog.Read");

        var response = await client.GetAsync($"/api/v1/catalog/objects/{Orders}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("Your own user grant text.");

        Description(await CallerStateAsync("S-A-USER", TenantA)).ShouldBe("Your own user grant text.");
    }

    [Fact]
    public async Task ExternalCatalog_RowScopeDescription_UserVsGroupGrant_EndToEnd()
    {
        Description(await CallerStateAsync("S-A-USER", TenantA, GroupSid)).ShouldBe("Your own user grant text.");
        Description(await CallerStateAsync("S-A-MEMBER", TenantA, GroupSid)).ShouldBe("Group grant text.");
        Description(await CallerStateAsync("S-A-STRANGER", TenantA)).ShouldBeNull();
    }
}
