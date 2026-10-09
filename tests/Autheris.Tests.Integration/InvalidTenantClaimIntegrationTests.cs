namespace Autheris.Tests.Integration;

using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// API-7: an invalid tenant claim is a 403, not a 500 from the authentication pipeline, and the raw claim value is not
/// echoed in the response.
/// </summary>
public sealed class InvalidTenantClaimIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InvalidTenantClaimIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task InvalidTenantClaim_Returns403_WithoutEchoingTheValue()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-TENANT-TEST");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "evil<script>tenant");

        var response = await client.GetAsync("/api/governance/sunsetting/rules");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        body.ShouldNotContain("evil");
    }
}
