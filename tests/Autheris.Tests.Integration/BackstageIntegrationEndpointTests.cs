using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class BackstageIntegrationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BackstageIntegrationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:Backstage:Enabled", "true");
            builder.UseSetting("Gateway:Backstage:DefaultOwner", "group:platform-governance");
            builder.UseSetting("Gateway:Backstage:DefaultSystem", "core-data-plane");
            builder.UseSetting("Gateway:Backstage:IncludeTablesAsApis", "true");
        });
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-BACKSTAGE-SYNC");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "CatalogSync,Reader");
        return client;
    }

    [Fact]
    public async Task GetCatalogEntities_Unauthenticated_ReturnsUnauthorized()
    {
        // Zero-Trust verification: Unauthenticated requests must be rejected with 401
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/integrations/backstage/catalog-entities");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCatalogEntities_ReturnsJsonArrayWithFederatedAndOpenApiApis()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/integrations/backstage/catalog-entities");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var entities = await response.Content.ReadFromJsonAsync<List<BackstageEntity>>();
        entities.ShouldNotBeNull();
        entities.Count.ShouldBeGreaterThanOrEqualTo(2);

        var federated = entities.FirstOrDefault(e => e.Metadata.Name == "autheris-federated");
        federated.ShouldNotBeNull();
        federated.Kind.ShouldBe("API");
        federated.Spec.Type.ShouldBe("graphql");
        federated.Spec.Owner.ShouldBe("group:platform-governance");
        federated.Spec.System.ShouldBe("core-data-plane");

        var openApi = entities.FirstOrDefault(e => e.Metadata.Name == "autheris-openapi");
        openApi.ShouldNotBeNull();
        openApi.Kind.ShouldBe("API");
        openApi.Spec.Type.ShouldBe("openapi");
    }

    [Fact]
    public async Task GetEntityByName_ExistingEntity_ReturnsSingleEntity()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/integrations/backstage/catalog-entities/autheris-federated");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entity = await response.Content.ReadFromJsonAsync<BackstageEntity>();
        entity.ShouldNotBeNull();
        entity.Metadata.Name.ShouldBe("autheris-federated");
        entity.Spec.Type.ShouldBe("graphql");
    }

    [Fact]
    public async Task GetEntityByName_UnknownEntity_ReturnsNotFound()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/integrations/backstage/catalog-entities/unknown-xyz-entity");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCatalogInfoYaml_ReturnsMultiDocumentYaml()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/integrations/backstage/catalog-info.yaml");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        mediaType.ShouldNotBeNull();
        mediaType!.ShouldContain("yaml");

        var yaml = await response.Content.ReadAsStringAsync();
        yaml.ShouldNotBeNullOrWhiteSpace();
        yaml.ShouldContain("---");
        yaml.ShouldContain("apiVersion: backstage.io/v1alpha1");
        yaml.ShouldContain("kind: API");
        yaml.ShouldContain("name: autheris-federated");
        yaml.ShouldContain("name: autheris-openapi");
    }

    [Fact]
    public async Task WhenDisabled_ReturnsNotFound()
    {
        // Arrange
        var disabledFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Backstage:Enabled", "false");
        });
        var client = disabledFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-BACKSTAGE-SYNC");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "CatalogSync,Reader");

        // Act
        var response = await client.GetAsync("/api/integrations/backstage/catalog-entities");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
