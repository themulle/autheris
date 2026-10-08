namespace Autheris.Tests.Integration;

using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-6: with introspection disabled, the SDL must not be downloadable via GET /graphql?sdl or /graphql/schema.graphql.
/// </summary>
public sealed class GraphQlSchemaDownloadIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GraphQlSchemaDownloadIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:GraphQL:EnableIntrospection", "false");
        });
    }

    [Theory]
    [InlineData("/graphql?sdl")]
    [InlineData("/graphql/schema.graphql")]
    public async Task SchemaDownload_IsDisabled_WhenIntrospectionIsDisabled(string url)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-GOV-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldNotContain("type Query");
        body.ShouldNotContain("schema {");
    }
}
