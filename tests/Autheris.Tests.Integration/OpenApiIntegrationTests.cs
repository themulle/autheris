namespace Autheris.Tests.Integration;

using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public sealed class OpenApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApiGlobalEndpoint_Returns200WithOpenApi31Json()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/$openapi");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("openapi").GetString().ShouldBe("3.1.0");
        doc.RootElement.GetProperty("paths").EnumerateObject().Any().ShouldBeTrue();
    }

    [Fact]
    public async Task OpenApiDomainEndpoint_Returns200WithDomainScopedJson()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/finance/openapi.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        foreach (var path in paths.EnumerateObject())
        {
            path.Name.ShouldStartWith("/finance/");
        }
    }

    [Fact]
    public async Task OpenApiYamlFormat_ReturnsYamlContentTypeAndValidStructure()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/$openapi?format=yaml");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/yaml");

        var yaml = await response.Content.ReadAsStringAsync();
        yaml.ShouldContain("openapi: \"3.1.0\"");
        yaml.ShouldContain("paths:");
    }

    [Fact]
    public async Task SwaggerUiEndpoint_ReturnsHtmlWithExplorer()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/odata/v4/$swagger");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("swagger-ui");
        html.ShouldContain("SwaggerUIBundle");
        html.ShouldContain("/odata/v4/$openapi");
    }

    [Fact]
    public async Task DocsEndpoint_ReturnsHtmlWithExplorer()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/docs");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("swagger-ui");
    }

    [Fact]
    public async Task OpenApiGlobalEndpoint_UnprivilegedUser_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-STANDARD-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "StandardUser");

        var response = await client.GetAsync("/odata/v4/$openapi");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IngestOpenApi_WithoutAdminRole_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-STANDARD-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "StandardUser");

        var content = new System.Net.Http.StringContent("openapi: 3.0.0\ninfo:\n  title: Test\n  version: 1.0.0\npaths: {}", System.Text.Encoding.UTF8, "application/yaml");
        var response = await client.PostAsync("/api/governance/catalog/ingest-openapi", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OpenApiIndexEndpoint_Returns200WithCatalogAndDomains()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/$openapi/index");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("totalDomains").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        root.GetProperty("totalTables").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        root.GetProperty("domains").EnumerateArray().Any(d => d.GetProperty("domain").GetString() == "finance").ShouldBeTrue();
    }

    [Fact]
    public async Task OpenApiModularMode_ReturnsRemoteComponentRefs()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/finance/openapi.json?mode=modular");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        var firstSchema = schemas.EnumerateObject().First();
        firstSchema.Value.TryGetProperty("$ref", out var refProp).ShouldBeTrue();
        refProp.GetString().ShouldStartWith("/odata/v4/$openapi/schemas/finance/");
    }

    [Fact]
    public async Task OpenApiEntitySchemaEndpoint_Returns200WithEntitySchema()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/odata/v4/$openapi/schemas/finance/dbo/finance_table_1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("object");
        doc.RootElement.GetProperty("properties").EnumerateObject().Any().ShouldBeTrue();
    }

    [Fact]
    public async Task OpenApiEndpoints_WhenOpenSchemaEnabled_AllowsAnonymousAccess()
    {
        using var openFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:OpenSchema", "true");
        });

        var anonymousClient = openFactory.CreateClient();

        // 1. Root OpenAPI
        var openApiResponse = await anonymousClient.GetAsync("/odata/v4/$openapi");
        openApiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. OpenAPI Index
        var indexResponse = await anonymousClient.GetAsync("/odata/v4/$openapi/index");
        indexResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Domain OpenAPI
        var domainResponse = await anonymousClient.GetAsync("/odata/v4/finance/openapi.json");
        domainResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 4. Standalone Schema
        var schemaResponse = await anonymousClient.GetAsync("/odata/v4/$openapi/schemas/finance/dbo/finance_table_1");
        schemaResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
