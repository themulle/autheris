namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

/// <summary>The self-grant route derives the self-grant fact server-side; a body flag never changes who may self-grant.</summary>
[Trait("Category", "Security")]
public sealed class SelfGrantApiIntegrationTests : IClassFixture<SelfGrantApiIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SelfGrantApiIntegrationTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("SelfGrantApiIntegrationTests"),
            });
        }
    }

    private HttpClient CreateClient(string roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-SELF");
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        return client;
    }

    private static StringContent Body(bool selfGrantFlag) => new(
        "{\"domain\":\"sales\",\"schema\":\"dbo\",\"tableName\":\"orders\",\"kind\":\"data\"," +
        "\"validTo\":\"" + DateTimeOffset.UtcNow.AddDays(30).ToString("O") + "\"," +
        "\"justification\":\"Break glass for incident 4711.\",\"selfGrant\":" + (selfGrantFlag ? "true" : "false") + "}",
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task Body_selfgrant_true_ignored()
    {
        // A governance admin is not an IT administrator: the flag in the body must not open the IT self-grant route.
        using var client = CreateClient("GovernanceAdmin");
        var response = await client.PostAsync("/api/v1/consent/self-grants", Body(selfGrantFlag: true));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The flag is not part of the contract: an IT administrator is judged on identity and ownership facts only.
        using var it = CreateClient("TenantAdmin");
        var withFlag = await it.PostAsync("/api/v1/consent/self-grants", Body(selfGrantFlag: true));
        var withoutFlag = await it.PostAsync("/api/v1/consent/self-grants", Body(selfGrantFlag: false));
        withFlag.StatusCode.ShouldBe(withoutFlag.StatusCode);
    }
}
