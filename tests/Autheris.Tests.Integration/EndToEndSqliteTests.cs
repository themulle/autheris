namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// End-to-End: HTTP -> ASP.NET-Pipeline (CSRF, Auth, Governance) -> Governed SQL / Endpoints -> echte SQLite-Datei.
/// Jedes Feature wird einmal durchlaufen. Die Datenquelle "default" ist eine temporaere SQLite-Datei mit
/// bekannten Kran-Daten, damit echte (nicht synthetische) Zeilen geprueft werden koennen.
/// Lauf: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~EndToEndSqlite"
/// </summary>
public sealed class EndToEndSqliteTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly WebApplicationFactory<Program> _factory;

    public EndToEndSqliteTests(WebApplicationFactory<Program> factory)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-e2e-" + Guid.NewGuid().ToString("N"));
        var queriesDir = Path.Combine(_workDir, "queries");
        Directory.CreateDirectory(queriesDir);
        _dbPath = Path.Combine(_workDir, "data.db");

        SeedSqlite(_dbPath);

        File.WriteAllText(Path.Combine(queriesDir, "cranes_by_type.sql"),
            "-- @name cranes_by_type\n-- @datasource default\n-- @summary Krane nach Typ\n-- @method GET\n\n" +
            "SELECT serial_number, crane_type, tonnage FROM e2e_cranes WHERE crane_type = @crane_type ORDER BY serial_number");

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:OpenSchema", "true");

            // Echte Datenquelle: SQLite-Datei
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dbPath}");

            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:AllowDml", "false");
            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");

            b.UseSetting("Gateway:SqlEndpoints:Enabled", "true");
            b.UseSetting("Gateway:SqlEndpoints:Directory", queriesDir);
            b.UseSetting("Gateway:SqlEndpoints:EnableHotReload", "false");
            b.UseSetting("Gateway:SqlEndpoints:AutoSyncFromDbt", "false");

            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Backstage:Enabled", "true");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private static void SeedSqlite(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE e2e_cranes (id INTEGER PRIMARY KEY, serial_number TEXT NOT NULL, crane_type TEXT NOT NULL, tonnage INTEGER NOT NULL);
INSERT INTO e2e_cranes VALUES (1,'LTM-1100','Mobilkran',100),(2,'LTM-1230','Mobilkran',230),(3,'LR-1600','Raupenkran',600),(4,'EC-B 125','Turmkran',12);";
        cmd.ExecuteNonQuery();
    }

    private async Task RegisterCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "e2e_cranes", TableName = "e2e_cranes" },
            Identifier = new TableIdentifier("default", "main", "e2e_cranes"),
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "serial_number", DataType = "varchar" },
                new() { ColumnName = "crane_type", DataType = "varchar" },
                new() { ColumnName = "tonnage", DataType = "integer" },
            }
        });
    }

    private HttpClient Client(bool authenticated = true, string roles = "Reader")
    {
        var c = _factory.CreateClient();
        if (authenticated)
        {
            c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-E2E-USER");
            c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            c.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_e2e");
        }
        return c;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    // ---------- 1. Health ----------
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_IsAnonymouslyReachable(string path)
    {
        var r = await Client(authenticated: false).GetAsync(path);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- 2. Auth ----------
    [Fact]
    public async Task Auth_WithoutCredentials_Returns401()
    {
        var r = await Client(authenticated: false).PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT 1" });
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_Session_ReturnsOkForAuthenticatedUser()
    {
        var r = await Client().GetAsync("/api/auth/session");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- 3. WebSQL -> SQLite ----------
    [Fact]
    public async Task WebSql_Select_ReturnsRealRowsFromSqlite()
    {
        await RegisterCatalogAsync();
        var r = await Client().PostAsJsonAsync("/api/v1/sql", new
        {
            sql = "SELECT serial_number, tonnage FROM e2e_cranes WHERE crane_type = 'Mobilkran' ORDER BY tonnage"
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("LTM-1100");
        body.ShouldContain("LTM-1230");
        body.ShouldNotContain("LR-1600");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task WebSql_Ddl_And_Dml_AreRejected_And_DataStaysUntouched()
    {
        await RegisterCatalogAsync();
        var c = Client();
        (await c.PostAsJsonAsync("/api/v1/sql", new { sql = "DROP TABLE e2e_cranes" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync("/api/v1/sql", new { sql = "DELETE FROM e2e_cranes" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM e2e_cranes";
        Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(4);
    }

    [Fact]
    public async Task WebSql_UncatalogedTable_IsRejected()
    {
        var r = await Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT * FROM not_in_catalog" });
        ((int)r.StatusCode).ShouldBeInRange(400, 403);
    }

    [Fact]
    public async Task WebSql_LegacyAlias_ApiSql_Works()
    {
        await RegisterCatalogAsync();
        var r = await Client().PostAsJsonAsync("/api/sql", new { sql = "SELECT COUNT(*) AS n FROM e2e_cranes" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("4");
    }

    // ---------- 4. Deklarative SQL-Endpunkte ----------
    [Fact]
    public async Task DeclarativeEndpoint_ListsAndExecutesAgainstSqlite()
    {
        await RegisterCatalogAsync();
        var c = Client();

        var list = await c.GetAsync("/api/v1/queries/");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).ShouldContain("cranes_by_type");

        var run = await c.GetAsync("/api/v1/queries/cranes_by_type?crane_type=Raupenkran");
        run.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await run.Content.ReadAsStringAsync();
        body.ShouldContain("LR-1600");
        body.ShouldNotContain("LTM-1100");
    }

    [Fact]
    public async Task DeclarativeEndpoint_UnknownName_Returns404()
    {
        var r = await Client().GetAsync("/api/v1/queries/does_not_exist");
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeclarativeEndpoint_OpenApiJson_DescribesEndpoint()
    {
        var r = await Client().GetAsync("/api/v1/queries/openapi.json");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("cranes_by_type");
    }

    // ---------- 5. OData / OpenAPI ----------
    [Fact]
    public async Task OpenApi_IndexJsonAndYaml_AreServedWithValidUrls()
    {
        var c = Client();
        var index = await c.GetAsync("/odata/v4/$openapi/index");
        index.StatusCode.ShouldBe(HttpStatusCode.OK);
        var indexBody = await index.Content.ReadAsStringAsync();
        indexBody.ShouldNotContain("/odata/v4/odata/v4/"); // Regression: doppelter Pfad

        (await c.GetAsync("/api/v1/openapi/index")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await c.GetAsync("/odata/v4/finance/openapi.json");
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var doc = await JsonAsync(json)) doc.RootElement.TryGetProperty("paths", out _).ShouldBeTrue();

        var yaml = await c.GetAsync("/odata/v4/finance/openapi.yaml");
        yaml.StatusCode.ShouldBe(HttpStatusCode.OK);
        var y = await yaml.Content.ReadAsStringAsync();
        y.ShouldContain("openapi:");
        y.ShouldContain("paths:");
    }

    [Fact]
    public async Task OData_ServiceDocumentAndMetadata_AreServed()
    {
        var c = Client();
        (await c.GetAsync("/odata/v4")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var meta = await c.GetAsync("/odata/v4/$metadata");
        meta.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await meta.Content.ReadAsStringAsync()).ShouldContain("edmx");
    }

    // ---------- 6. GraphQL + CSRF ----------
    [Fact]
    public async Task GraphQL_WithCsrfHeader_Works_WithoutHeader_IsRejected()
    {
        var c = Client();
        var content = () => new StringContent("{\"query\":\"{ __typename }\"}", Encoding.UTF8, "application/json");

        var bad = await c.PostAsync("/graphql", content());
        ((int)bad.StatusCode).ShouldBeInRange(400, 403);

        c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var ok = await c.PostAsync("/graphql", content());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync()).ShouldContain("Query");
    }

    // ---------- 7. Governance-DB / Backstage-Export ----------
    [Fact]
    public async Task Backstage_Export_ListsCatalogedTable()
    {
        await RegisterCatalogAsync();
        var c = Client(roles: "CatalogSync,Reader");
        var r = await c.GetAsync("/api/integrations/backstage/catalog-entities");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("e2e_cranes");

        var yaml = await c.GetAsync("/api/integrations/backstage/catalog-info.yaml");
        yaml.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- 8. MCP ----------
    [Fact]
    public async Task Mcp_Initialize_IsReachableAndAuthenticated()
    {
        var anon = Client(authenticated: false);
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"e2e\",\"version\":\"1\"}}}";
        (await anon.PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var r = await Client().PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));
        ((int)r.StatusCode).ShouldBeLessThan(500);
        r.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    // ---------- 9. Admin-/Rollen-Endpunkte (Zugriffsschutz) ----------
    [Theory]
    [InlineData("GET", "/api/extensions/dbt/exposures")]
    [InlineData("GET", "/api/governance/sunsetting/rules")]
    [InlineData("GET", "/api/schema-registry/services")]
    [InlineData("GET", "/api/v1/cdc/subscriptions")]
    public async Task RoleProtectedEndpoints_RejectPlainReader_AcceptAdmin(string method, string path)
    {
        var plain = await Client(roles: "Reader").SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        ((int)plain.StatusCode).ShouldBeOneOf(401, 403);

        var admin = await Client(roles: "GovernanceAdmin,ClusterAdmin,SchemaAdmin,StreamingAdmin,DbtAdmin")
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        ((int)admin.StatusCode).ShouldBeLessThan(500);
        admin.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        admin.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    // ---------- 10. Dev-Endpunkte ----------
    [Fact]
    public async Task Dev_Info_And_GettingStarted_AreServedInDevelopment()
    {
        var c = Client(authenticated: false);
        (await c.GetAsync("/getting-started")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.GetAsync("/docs")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
