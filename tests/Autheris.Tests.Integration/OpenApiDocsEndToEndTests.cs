namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// End-to-End "Dokumentation ist wirklich verfuegbar": SQLite-Tabelle -> Katalog -> OpenAPI/Docs-UI -> jeder
/// verlinkte Spec ist abrufbar, parsebar, in sich konsistent und beschreibt Endpunkte, die es tatsaechlich gibt.
/// Regressionen, die hier auffallen sollen: doppelte Pfade (/odata/v4/odata/v4/...), YAML nicht parsebar,
/// Index verlinkt tote URLs, $ref ins Leere, dokumentierter Endpunkt liefert 404.
/// Swagger UI wird lokal ausgeliefert (/ui/swagger/assets/*) und ist damit auch ohne Internet/Proxy nutzbar.
/// </summary>
public sealed class OpenApiDocsEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _workDir;
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiDocsEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-openapi-e2e-" + Guid.NewGuid().ToString("N"));
        var queriesDir = Path.Combine(_workDir, "queries");
        Directory.CreateDirectory(queriesDir);
        var dbPath = Path.Combine(_workDir, "data.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE e2e_cranes (id INTEGER PRIMARY KEY, serial_number TEXT NOT NULL, crane_type TEXT NOT NULL);
INSERT INTO e2e_cranes VALUES (1,'LTM-1100','Mobilkran'),(2,'LR-1600','Raupenkran');";
            cmd.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(queriesDir, "cranes_by_type.sql"),
            "-- @name cranes_by_type\n-- @datasource default\n-- @summary Krane nach Typ\n-- @method GET\n\n" +
            "SELECT serial_number, crane_type FROM e2e_cranes WHERE crane_type = @crane_type ORDER BY serial_number");

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "2000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dbPath}");
            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
            b.UseSetting("Gateway:SqlEndpoints:Enabled", "true");
            b.UseSetting("Gateway:SqlEndpoints:Directory", queriesDir);
            b.UseSetting("Gateway:SqlEndpoints:EnableHotReload", "false");
            b.UseSetting("Gateway:SqlEndpoints:AutoSyncFromDbt", "false");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private HttpClient Admin()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-OPENAPI-ADMIN");
        c.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,SchemaAdmin");
        return c;
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
            }
        });
    }

    /// <summary>Macht aus absoluten oder relativen Links einen Pfad fuer den In-Memory-Client.</summary>
    private static string ToRelative(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs.PathAndQuery : url;

    private static IEnumerable<string> CollectRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (k == "$ref" && v is JsonValue jv && jv.TryGetValue<string>(out var s)) yield return s;
                    foreach (var r in CollectRefs(v)) yield return r;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var r in CollectRefs(item)) yield return r;
                break;
        }
    }

    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        JsonNode? cur = root;
        foreach (var part in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = part.Replace("~1", "/").Replace("~0", "~");
            cur = cur is JsonObject o && o.TryGetPropertyValue(key, out var next) ? next : null;
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Spec muss in sich stimmig sein (Version, Titel, Pfade, Responses, interne und externe $refs).</summary>
    private async Task AssertSpecIsSoundAsync(HttpClient client, string label, string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        (root["openapi"]?.GetValue<string>() ?? root["swagger"]?.GetValue<string>()).ShouldNotBeNullOrWhiteSpace($"{label}: keine openapi-Version");
        root["info"]?["title"]?.GetValue<string>().ShouldNotBeNullOrWhiteSpace($"{label}: info.title fehlt");
        root["paths"].ShouldNotBeNull($"{label}: paths fehlt");

        foreach (var (path, item) in root["paths"]!.AsObject())
        {
            path.ShouldStartWith("/", customMessage: $"{label}: Pfad ohne fuehrenden Slash: {path}");
            foreach (var (verb, op) in item!.AsObject())
            {
                if (verb is "get" or "post" or "put" or "patch" or "delete")
                {
                    op!["responses"].ShouldNotBeNull($"{label}: {verb} {path} ohne responses");
                }
            }
        }

        foreach (var reference in CollectRefs(root).Distinct())
        {
            if (reference.StartsWith('#'))
            {
                ResolvePointer(root, reference).ShouldNotBeNull($"{label}: $ref ins Leere: {reference}");
            }
            else if (reference.StartsWith('/') || reference.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var r = await client.GetAsync(ToRelative(reference));
                r.StatusCode.ShouldBe(HttpStatusCode.OK, $"{label}: externe $ref nicht abrufbar: {reference}");
            }
        }
    }

    // ---------- Kette: Index -> jede verlinkte Spec ----------
    [Fact]
    public async Task OpenApiIndex_EveryLinkedSpecIsReachableValidAndHasNoDoubledPrefix()
    {
        await RegisterCatalogAsync();
        var client = Admin();

        var index = await client.GetAsync("/odata/v4/$openapi/index");
        index.StatusCode.ShouldBe(HttpStatusCode.OK);
        var indexJson = await index.Content.ReadAsStringAsync();
        indexJson.ShouldNotContain("/odata/v4/odata/v4", customMessage: "Index verlinkt doppelten Pfad");

        using var doc = JsonDocument.Parse(indexJson);
        var urls = new List<string>();
        foreach (var api in doc.RootElement.GetProperty("apis").EnumerateArray()) urls.Add(api.GetProperty("url").GetString()!);
        foreach (var d in doc.RootElement.GetProperty("domains").EnumerateArray())
        {
            urls.Add(d.GetProperty("jsonUrl").GetString()!);
            urls.Add(d.GetProperty("yamlUrl").GetString()!);
        }
        urls.ShouldNotBeEmpty();

        foreach (var url in urls.Distinct())
        {
            var response = await client.GetAsync(ToRelative(url));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"Index-Link nicht abrufbar: {url}");
            var body = await response.Content.ReadAsStringAsync();
            body.ShouldNotBeNullOrWhiteSpace($"leere Antwort: {url}");

            if (url.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                body.ShouldContain("openapi:", customMessage: $"YAML ohne openapi-Kopf: {url}");
                body.ShouldContain("paths:", customMessage: $"YAML ohne paths: {url}");
            }
            else
            {
                await AssertSpecIsSoundAsync(client, url, body);
            }
        }

        // Der Alias liefert dasselbe Verzeichnis
        (await client.GetAsync("/api/v1/openapi/index")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- SQLite-Tabelle taucht in der Doku auf und der dokumentierte Endpunkt existiert ----------
    [Fact]
    public async Task CatalogedSqliteTable_IsDocumented_AndDocumentedPathExists()
    {
        await RegisterCatalogAsync();
        var client = Admin();

        var json = await (await client.GetAsync("/odata/v4/default/openapi.json")).Content.ReadAsStringAsync();
        var spec = JsonNode.Parse(json)!.AsObject();
        var paths = spec["paths"]!.AsObject().Select(p => p.Key).ToList();
        var tablePath = paths.FirstOrDefault(p => p.Contains("e2e_cranes", StringComparison.OrdinalIgnoreCase));
        tablePath.ShouldNotBeNull("e2e_cranes nicht in der Domain-Spec dokumentiert");

        // YAML-Variante enthaelt denselben Pfad
        var yaml = await (await client.GetAsync("/odata/v4/default/openapi.yaml")).Content.ReadAsStringAsync();
        yaml.ShouldContain(tablePath!);

        // Dokumentierter Pfad (relativ zu servers[0].url bzw. /odata/v4) ist kein 404 / 5xx
        var server = spec["servers"]?.AsArray().FirstOrDefault()?["url"]?.GetValue<string>() ?? "/odata/v4";
        var call = await client.GetAsync(ToRelative(server).TrimEnd('/') + tablePath);
        call.StatusCode.ShouldNotBe(HttpStatusCode.NotFound, "dokumentierter OData-Pfad existiert nicht");
        ((int)call.StatusCode).ShouldBeLessThan(500);

        // Entity-Schema separat abrufbar
        var schema = await client.GetAsync("/odata/v4/$openapi/schemas/default/main/e2e_cranes");
        schema.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- Deklarative SQL-Endpunkte: Spec <-> Realitaet ----------
    [Fact]
    public async Task DeclarativeEndpointSpec_DescribesExecutableEndpoint()
    {
        await RegisterCatalogAsync();
        var client = Admin();

        var r = await client.GetAsync("/api/v1/queries/openapi.json");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await r.Content.ReadAsStringAsync();
        await AssertSpecIsSoundAsync(client, "queries/openapi.json", json);

        var spec = JsonNode.Parse(json)!.AsObject();
        var path = spec["paths"]!.AsObject().Select(p => p.Key).FirstOrDefault(p => p.EndsWith("cranes_by_type"));
        path.ShouldNotBeNull("cranes_by_type nicht in der Spec");

        var parameters = spec["paths"]![path!]!["get"]!["parameters"]!.AsArray();
        parameters.Any(p => p!["name"]!.GetValue<string>() == "crane_type").ShouldBeTrue("Parameter crane_type nicht dokumentiert");

        // Der dokumentierte Aufruf funktioniert und liefert die Daten aus SQLite
        var url = path.StartsWith("/api/") ? path : "/api/v1/queries" + path;
        var call = await client.GetAsync(url + "?crane_type=Raupenkran");
        call.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await call.Content.ReadAsStringAsync()).ShouldContain("LR-1600");
    }

    // ---------- Docs-UI verweist nur auf lebende Spec-URLs ----------
    [Theory]
    [InlineData("/ui/swagger")]
    [InlineData("/docs")]
    [InlineData("/docs?domain=default")]
    [InlineData("/odata/v4/$swagger")]
    public async Task SwaggerUi_ReferencesOnlyReachableSpecs(string page)
    {
        await RegisterCatalogAsync();
        var client = Admin();

        var response = await client.GetAsync(page);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("swagger-ui");

        var refs = Regex.Matches(html, "[\"'](/[^\"'\\s<>]*(?:openapi|swagger)[^\"'\\s<>]*)[\"']")
            .Select(m => m.Groups[1].Value)
            .Where(u => !u.Contains("${") && !u.Contains("{{"))
            .Distinct()
            .ToList();
        refs.ShouldNotBeEmpty("Docs-Seite verweist auf keine OpenAPI-Quelle");

        foreach (var u in refs)
        {
            var r = await client.GetAsync(u);
            r.StatusCode.ShouldBe(HttpStatusCode.OK, $"Docs-Seite {page} verweist auf tote URL: {u}");
        }
    }

    // ---------- Offline: Swagger UI kommt komplett vom Gateway, nichts vom CDN ----------
    [Theory]
    [InlineData("/ui/swagger")]
    [InlineData("/docs")]
    [InlineData("/odata/v4/$swagger")]
    public async Task SwaggerUi_IsServedOffline_WithoutExternalHosts(string page)
    {
        var client = Admin();
        var response = await client.GetAsync(page);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        Regex.IsMatch(html, "(src|href)\\s*=\\s*[\"']https?://", RegexOptions.IgnoreCase)
            .ShouldBeFalse("Seite laedt Ressourcen von externen Hosts (nicht offline-faehig)");
        html.ShouldNotContain("unpkg.com");

        var csp = string.Join(";", response.Headers.TryGetValues("Content-Security-Policy", out var v) ? v : Array.Empty<string>());
        csp.ShouldNotContain("unpkg.com");
        csp.ShouldNotContain("http");

        foreach (var (file, minBytes, type) in new[]
        {
            ("swagger-ui.css", 50_000, "text/css"),
            ("swagger-ui-bundle.js", 500_000, "javascript"),
            ("swagger-ui-standalone-preset.js", 50_000, "javascript"),
        })
        {
            var asset = await _factory.CreateClient().GetAsync($"/ui/swagger/assets/{file}");
            asset.StatusCode.ShouldBe(HttpStatusCode.OK, $"Asset fehlt: {file}");
            asset.Content.Headers.ContentType!.MediaType!.ShouldContain(type);
            (await asset.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(minBytes, $"Asset zu klein/leer: {file}");
        }

        (await _factory.CreateClient().GetAsync("/ui/swagger/assets/does-not-exist.js")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.CreateClient().GetAsync("/ui/swagger/assets/..%2Fappsettings.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------- Reverse Proxy: Index-Links muessen zur externen Adresse passen ----------
    private static async Task<List<string>> IndexUrlsAsync(HttpClient client)
    {
        var r = await client.GetAsync("/odata/v4/$openapi/index");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var urls = new List<string>();
        foreach (var api in doc.RootElement.GetProperty("apis").EnumerateArray()) urls.Add(api.GetProperty("url").GetString()!);
        foreach (var d in doc.RootElement.GetProperty("domains").EnumerateArray())
        {
            urls.Add(d.GetProperty("jsonUrl").GetString()!);
            urls.Add(d.GetProperty("yamlUrl").GetString()!);
        }
        return urls;
    }

    private static HttpClient AdminOf(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-OPENAPI-ADMIN");
        c.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,SchemaAdmin");
        return c;
    }

    [Fact]
    public async Task OpenApiIndex_BehindTrustedReverseProxy_UsesExternalHostAndScheme()
    {
        await RegisterCatalogAsync();
        using var proxied = _factory.WithWebHostBuilder(b => b.UseSetting("Gateway:ReverseProxy:Enabled", "true"));
        var client = AdminOf(proxied);
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "docs.corp.local");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var urls = await IndexUrlsAsync(client);
        urls.ShouldNotBeEmpty();
        foreach (var url in urls.Where(u => Uri.TryCreate(u, UriKind.Absolute, out _)))
        {
            url.ShouldStartWith("https://docs.corp.local/odata/v4/", customMessage: $"Link passt nicht zur externen Adresse: {url}");
        }
        // Relative Links (z. B. SQL-Spec) bleiben host-neutral
        urls.ShouldContain("/api/v1/queries/openapi.json");
    }

    [Fact]
    public async Task OpenApiIndex_WithoutReverseProxy_IgnoresSpoofedForwardedHost()
    {
        await RegisterCatalogAsync();
        var client = Admin();
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example.org");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        foreach (var url in await IndexUrlsAsync(client))
        {
            url.ShouldNotContain("evil.example.org", customMessage: $"Index uebernimmt unvertrauten X-Forwarded-Host: {url}");
        }
    }

    // ---------- Zugriffsmodell der Doku ----------
    [Fact]
    public async Task OpenApi_WithoutOpenSchema_RequiresAuth_WithOpenSchema_IsPublic()
    {
        var anon = _factory.CreateClient();
        var closed = await anon.GetAsync("/odata/v4/$openapi/index");
        ((int)closed.StatusCode).ShouldBeOneOf(401, 403);

        using var open = _factory.WithWebHostBuilder(b => b.UseSetting("Gateway:OpenSchema", "true"));
        var publicClient = open.CreateClient();
        (await publicClient.GetAsync("/odata/v4/$openapi/index")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await publicClient.GetAsync("/api/v1/queries/openapi.json")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---------- GraphQL-Schema ist abrufbar (Introspection) ----------
    [Fact]
    public async Task GraphQL_Introspection_ReturnsSchemaWithoutErrors()
    {
        var client = Admin();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        var r = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ __schema { queryType { name } types { name } } }"
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out _).ShouldBeFalse($"Introspection liefert Fehler: {body}");
        doc.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("queryType").GetProperty("name").GetString().ShouldBe("Query");
        doc.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("types").GetArrayLength().ShouldBeGreaterThan(5);
    }
}
