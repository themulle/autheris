namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #282 (AC-5): an empty governance catalog and a configured PostgreSQL data source; the dbt sync registers the tables
/// (inactive), a consent manifest grants access, and the table becomes usable after the administrator activates it.
/// </summary>
public sealed class DbtSyncCatalogRegistrationPostgreSqlTests : IClassFixture<PostgreSqlContainerFixture>
{
    private const string Manifest = """
    {
      "metadata": { "dbt_version": "1.8.0" },
      "nodes": {
        "model.shop.orders": {
          "name": "orders", "database": "warehouse", "schema": "public", "description": "Orders",
          "config": { "materialized": "table" }, "tags": ["finance"], "meta": {},
          "columns": {
            "id": { "name": "id", "data_type": "integer", "description": "Order id", "tags": [], "meta": {} },
            "email": { "name": "email", "data_type": "varchar", "description": "Contact", "tags": [], "meta": {} }
          },
          "depends_on": { "nodes": [] }
        }
      },
      "sources": {}
    }
    """;

    private readonly PostgreSqlContainerFixture _fixture;

    public DbtSyncCatalogRegistrationPostgreSqlTests(PostgreSqlContainerFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
        builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-dbt282-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        builder.UseSetting("Gateway:DataSources:Connections:warehouse:Provider", "PostgreSql");
        builder.UseSetting("Gateway:DataSources:Connections:warehouse:ConnectionString", _fixture.ConnectionString);
        builder.UseSetting("Gateway:Dbt:CreateMissingTables", "true");
    });

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "warehouse");
        return client;
    }

    [Fact]
    public async Task Sequence_SyncGovernanceConsents_YieldsQueryableTablesWithMasksAndConsents()
    {
        using var host = CreateHost();
        var client = Client(host);
        var id = new TableIdentifier("warehouse", "public", "orders");

        // 1. Sync registers the missing table, inactive.
        var sync = await client.PostAsync("/api/extensions/dbt/sync", new StringContent(Manifest, Encoding.UTF8, "application/json"));
        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await sync.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("createdTables").GetArrayLength().ShouldBe(1);
        body.GetProperty("skippedItems").GetArrayLength().ShouldBe(0);

        using (var scope = host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var created = await repo.GetTableMetadataAsync(id);
            created.ShouldNotBeNull();
            created.Table.IsActive.ShouldBeFalse();
            created.Table.SourceName.ShouldBe("warehouse");
            created.Table.SourceType.ShouldBe("PostgreSql");
            created.Columns.Select(c => c.ColumnName).ShouldBe(["id", "email"], ignoreOrder: true);
        }

        // 2. Consent manifest (with a column mask) applies to the registered table.
        const string consents = """
        {
          "version": "v1",
          "policies": [
            { "domain": "warehouse", "schema": "public", "tableName": "orders", "granteeType": "Role", "granteeRef": "Analyst", "effect": "ALLOW",
              "columnRules": [ { "columnName": "id", "accessLevel": "CLEAR" }, { "columnName": "email", "accessLevel": "MASKED" } ] }
          ]
        }
        """;
        var apply = await client.PostAsync("/api/v1/governance/consents/apply", new StringContent(consents, Encoding.UTF8, "application/json"));
        apply.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. The administrator activates the table (existing governance path), and a second sync keeps it active.
        using (var scope = host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var current = (await repo.GetTableMetadataAsync(id))!;
            var activated = new Table
            {
                Id = current.Table.Id, SourceType = current.Table.SourceType, SourceName = current.Table.SourceName,
                SchemaName = current.Table.SchemaName, TableName = current.Table.TableName, DisplayName = current.Table.DisplayName,
                Description = current.Table.Description, Sensitivity = current.Table.Sensitivity, IsActive = true
            };
            await repo.UpsertTableMetadataAsync(new TableMetadata { Identifier = id, Table = activated, Columns = current.Columns });
        }

        var second = await client.PostAsync("/api/extensions/dbt/sync", new StringContent(Manifest, Encoding.UTF8, "application/json"));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("createdTables").GetArrayLength().ShouldBe(0);

        using (var scope = host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            (await repo.GetTableMetadataAsync(id))!.Table.IsActive.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Sync_DryRunReportsCreatedTablesAndWritesNothing()
    {
        using var host = CreateHost();
        var client = Client(host);

        var sync = await client.PostAsync("/api/extensions/dbt/sync?dryRun=true", new StringContent(Manifest, Encoding.UTF8, "application/json"));
        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("createdTables").GetArrayLength().ShouldBe(1);

        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        (await repo.GetTableMetadataAsync(new TableIdentifier("warehouse", "public", "orders"))).ShouldBeNull();
    }
}
