namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// BUG #157 (PLAN-BUG-157 WP-3): a catalog table without queryable columns no longer stops the host; the omission is listed in the
/// system health endpoint for administrators only. The host starts twice over one SQLite file: the first start seeds the catalog,
/// the second start builds the GraphQL schema from a catalog that already contains the column-less tables. The metadata model, the
/// GraphQL schema and the health endpoint are the real ones (SEC-BUG157-10), no stubs.
/// </summary>
public sealed class ColumnlessCatalogTableStartupTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("dom_a", "main", "orders_a");

    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"autheris-colless-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteCleanup();
    }

    private void SqliteCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbFile) + "*"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private WebApplicationFactory<Program> Create(bool systemMetricsEnabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={_dbFile}");
            b.UseSetting("Gateway:Tenancy:Mode", "Multi");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            b.UseSetting("Gateway:SystemMetrics:Enabled", systemMetricsEnabled ? "true" : "false");
        });

    private static TableMetadata Meta(TableIdentifier id, params TableColumn[] columns) => new()
    {
        Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "Sqlite", SourceName = id.Domain },
        Identifier = id,
        Columns = columns.ToList()
    };

    /// <summary>Seeds one valid table plus <paramref name="omitted"/> column-less tables, then starts a fresh host over the same file.</summary>
    private async Task<WebApplicationFactory<Program>> StartSeededAsync(int omitted, bool systemMetricsEnabled = true)
    {
        using (var seeding = Create())
        {
            using var scope = seeding.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.UpsertTableMetadataAsync(Meta(Orders, new TableColumn { ColumnName = "id", DataType = "integer" }));
            for (var i = 0; i < omitted; i++)
            {
                // Mix of zero columns and columns that all fail the plain-identifier rule (R-GQL-13).
                var columns = i % 2 == 0 ? Array.Empty<TableColumn>() : [new TableColumn { ColumnName = "secret-col-" + i, DataType = "integer" }];
                await repo.UpsertTableMetadataAsync(Meta(new TableIdentifier("dom_x", "main", $"empty_{i:D3}"), columns));
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return Create(systemMetricsEnabled);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-a"); // DT-A-3: a tenant-less caller is refused at the gate (Multi, no default)
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> HealthAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/governance/system/health");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HostStart_WithColumnlessCatalogTable_ReachesReadyAndAnswersGraphQlOnOtherTable()
    {
        using var factory = await StartSeededAsync(omitted: 2);
        var client = Client(factory, "GovernanceAdmin");

        var response = await client.PostAsync("/graphql", new StringContent(
            "{\"query\":\"{ __schema { queryType { fields { name } } } }\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("dom_a_main_orders_a");
        body.ShouldNotContain("dom_x_main_empty_000");
        body.ShouldNotContain("SchemaException");
    }

    [Fact]
    public async Task Health_Admin_ListsOmittedTableWithReason_StatusUnaffected()
    {
        using var factory = await StartSeededAsync(omitted: 2);

        var (_, body) = await HealthAsync(Client(factory, "GovernanceAdmin"));

        using var doc = JsonDocument.Parse(body);
        var catalog = doc.RootElement.GetProperty("graphQlCatalog");
        catalog.GetProperty("omittedTableCount").GetInt32().ShouldBe(2);
        catalog.GetProperty("omittedTablesTruncated").GetBoolean().ShouldBeFalse();
        var entries = catalog.GetProperty("omittedTables").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("table").GetString()).ShouldBe(["dom_x.main.empty_000", "dom_x.main.empty_001"]);
        entries.ShouldAllBe(e => e.GetProperty("reason").GetString() == "NoQueryableColumns");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_NonAdminAllowedRole_GetsCountWithoutTableNames()
    {
        // SEC-BUG157-02 / T-BUG157-S03: SecurityAdmin passes the existing gate (default roles) but does not imply GovernanceAdmin.
        using var factory = await StartSeededAsync(omitted: 2);

        var (_, body) = await HealthAsync(Client(factory, "SecurityAdmin"));

        using var doc = JsonDocument.Parse(body);
        var catalog = doc.RootElement.GetProperty("graphQlCatalog");
        catalog.GetProperty("omittedTableCount").GetInt32().ShouldBe(2);
        catalog.TryGetProperty("omittedTables", out _).ShouldBeFalse();
        body.ShouldNotContain("empty_00");
        body.ShouldNotContain("dom_x");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_Admin_EntriesContainOnlyTableAndReason()
    {
        // SEC-BUG157-03 / T-BUG157-S04
        using var factory = await StartSeededAsync(omitted: 2);

        var (_, body) = await HealthAsync(Client(factory, "GovernanceAdmin"));

        using var doc = JsonDocument.Parse(body);
        var catalog = doc.RootElement.GetProperty("graphQlCatalog");
        catalog.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["omittedTableCount", "omittedTables", "omittedTablesTruncated"]);
        foreach (var entry in catalog.GetProperty("omittedTables").EnumerateArray())
        {
            entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["reason", "table"]);
        }

        catalog.GetRawText().ShouldNotContain("secret-col");
        catalog.GetRawText().ShouldNotContain("Sqlite", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_NonAdmin403_AndDisabled404_ExposeNoTableNames()
    {
        // SEC-BUG157-02 / T-BUG157-S05
        using (var factory = await StartSeededAsync(omitted: 2))
        {
            var (status, body) = (await HealthAsync(Client(factory, "Reader")));
            status.ShouldBe(HttpStatusCode.Forbidden);
            body.ShouldNotContain("empty_00");
            body.ShouldNotContain("dom_x");
            body.ShouldNotContain("graphQlCatalog");
        }

        using var disabled = Create(systemMetricsEnabled: false);
        var (disabledStatus, disabledBody) = await HealthAsync(Client(disabled, "GovernanceAdmin"));
        disabledStatus.ShouldBe(HttpStatusCode.NotFound);
        disabledBody.ShouldNotContain("empty_00");
        disabledBody.ShouldNotContain("dom_x");
        disabledBody.ShouldNotContain("graphQlCatalog");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_MoreThan100Omissions_CapsListDeterministicallyAndReportsFullCount()
    {
        // SEC-BUG157-03 / T-BUG157-S06
        using var factory = await StartSeededAsync(omitted: 105);

        var (_, body) = await HealthAsync(Client(factory, "GovernanceAdmin"));

        using var doc = JsonDocument.Parse(body);
        var catalog = doc.RootElement.GetProperty("graphQlCatalog");
        catalog.GetProperty("omittedTableCount").GetInt32().ShouldBe(105);
        catalog.GetProperty("omittedTablesTruncated").GetBoolean().ShouldBeTrue();
        var names = catalog.GetProperty("omittedTables").EnumerateArray().Select(e => e.GetProperty("table").GetString()!).ToList();
        names.Count.ShouldBe(100);
        names.ShouldBe(Enumerable.Range(0, 100).Select(i => $"dom_x.main.empty_{i:D3}").ToList());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_Omissions_DoNotChangeStatusOrStatusCode()
    {
        // SEC-BUG157-04 / T-BUG157-S07: a host with omissions answers like a host without them.
        HttpStatusCode withoutStatus;
        string withoutHealth;
        using (var clean = await StartSeededAsync(omitted: 0))
        {
            var (status, body) = await HealthAsync(Client(clean, "GovernanceAdmin"));
            withoutStatus = status;
            using var doc = JsonDocument.Parse(body);
            withoutHealth = doc.RootElement.GetProperty("status").GetString()!;
        }

        using var dirty = await StartSeededAsync(omitted: 3);
        var (dirtyStatus, dirtyBody) = await HealthAsync(Client(dirty, "GovernanceAdmin"));
        using var dirtyDoc = JsonDocument.Parse(dirtyBody);

        dirtyStatus.ShouldBe(withoutStatus);
        dirtyDoc.RootElement.GetProperty("status").GetString().ShouldBe(withoutHealth);
    }
}
