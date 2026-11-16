namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// API-2: the JSON ext_authz endpoint decided with the identity of the calling mesh service (confused deputy)
/// and is no longer reachable.
/// </summary>
public sealed class EnvoyJsonModeRemovedIntegrationTests : IClassFixture<EnvoyJsonModeRemovedIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EnvoyJsonModeRemovedIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("EnvoyJsonModeRemovedIntegrationTests"),
            });
        }
    }

    [Fact]
    public async Task JsonCheckEndpoint_IsNotMapped()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-MESH-SERVICE");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.PostAsJsonAsync("/api/v1/envoy/authz", new { attributes = new { request = new { http = new { method = "GET", path = "/api/orders" } } } });

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }
}
