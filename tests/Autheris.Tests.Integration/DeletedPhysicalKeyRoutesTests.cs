namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-44 PR 4: verifies both deleted refresh-physical-keys routes return 404.</summary>
public sealed class DeletedPhysicalKeyRoutesTests : IClassFixture<DeletedPhysicalKeyRoutesTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeletedPhysicalKeyRoutesTests(HostFixture factory)
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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("DeletedPhysicalKeyRoutesTests"),
            });
        }
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-PHYS-REMOVED");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        return client;
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("/api/v1/catalog/datasources/ds1/refresh-physical-keys")]
    [InlineData("/api/governance/datasources/ds1/refresh-physical-keys")]
    public async Task RefreshPhysicalKeys_BothRoutes_404(string url)
    {
        var response = await CreateAdminClient().PostAsJsonAsync(url, new { dryRun = true });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
