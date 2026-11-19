namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>Issue #856: the external catalog routes keep answering 401 to a caller without a token, over a real host.</summary>
[Trait("Category", "Security")]
public sealed class CatalogObjectsAnonymousIntegrationTests : IClassFixture<CatalogObjectsAnonymousIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogObjectsAnonymousIntegrationTests(HostFixture factory)
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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("CatalogObjectsAnonymousIntegrationTests"),
            });
        }
    }

    [Theory]
    [InlineData("/api/v1/catalog/objects")]
    [InlineData("/api/v1/catalog/objects/pg.dbo.orders")]
    [InlineData("/api/v1/catalog/changes")]
    public async Task Anonymous_Returns401(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
