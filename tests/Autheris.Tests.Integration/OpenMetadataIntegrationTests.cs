using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class OpenMetadataIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IOpenMetadataClient _mockOmClient = Substitute.For<IOpenMetadataClient>();

    public OpenMetadataIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:OpenMetadata:WebhookSecret", "test-webhook-secret-999");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IOpenMetadataClient>(_mockOmClient);
            });
        });
    }

    [Fact]
    public async Task Webhook_WithoutSignature_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var content = new StringContent("{\"eventType\":\"entityCreated\",\"entityType\":\"table\"}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/webhooks/openmetadata", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_WithValidSignature_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var eventId = Guid.NewGuid();
        var payload = $"{{\"id\":\"{eventId}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"service.db.schema.customers\"}}";

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-webhook-secret-999"), Encoding.UTF8.GetBytes(payload));
        var hex = Convert.ToHexStringLower(hash);

        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "customers",
            FullyQualifiedName = "service.db.schema.customers"
        };

        _mockOmClient.GetTableByFqnAsync("service.db.schema.customers", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openmetadata")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-OpenMetadata-Signature", $"sha256={hex}");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GraphQL_SyncOpenMetadataMutation_ForbiddenForNonAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-NON-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "StandardUser");

        var mutation = new
        {
            query = "mutation { syncOpenMetadata(dryRun: true) { success syncedTables } }"
        };

        var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(mutation), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("FORBIDDEN");
    }

    [Fact]
    public async Task GraphQL_SyncOpenMetadataMutation_AllowedForGovernanceAdmin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        _mockOmClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([]));
        _mockOmClient.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]));
        _mockOmClient.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]));
        _mockOmClient.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        _mockOmClient.GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        var mutation = new
        {
            query = "mutation { syncOpenMetadata(dryRun: true) { success syncedTables } }"
        };

        var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(mutation), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"success\":true");
    }
}
