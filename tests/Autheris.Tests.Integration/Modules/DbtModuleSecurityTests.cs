namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>Host with module <c>dbt</c> enabled: module routes stay authenticated, the signed webhook reaches the module handler.</summary>
[Trait("Category", "Security")]
public sealed class DbtModuleSecurityTests : IClassFixture<DbtModuleSecurityTests.HostFixture>
{
    private const string Secret = "dbt-module-security-test-secret";

    private readonly WebApplicationFactory<Program> _factory;

    public DbtModuleSecurityTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "DbtModuleSecurity");
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", "tenant-1");
            builder.UseSetting("Gateway:Dbt:WebhookSecret", Secret);
            DbtWorkspaceTestSettings.Apply((k, v) => builder.UseSetting(k, v));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Anonymous_module_dbt_route_returns_401()
    {
        using var client = _factory.CreateClient();

        using var health = await client.GetAsync("/modules/dbt/health");
        health.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var sync = await client.PostAsync("/modules/dbt/sync", new StringContent("{}", Encoding.UTF8, "application/json"));
        sync.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var legacy = await client.GetAsync("/api/extensions/dbt/health");
        legacy.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signed_webhook_reaches_handler_when_enabled()
    {
        using var client = _factory.CreateClient();
        var payload = "{\"eventId\":\"evt_" + System.Guid.NewGuid().ToString("N") + "\",\"eventType\":\"job_run.completed\",\"timestamp\":\""
            + System.DateTimeOffset.UtcNow.ToString("O") + "\",\"data\":{\"runId\":42,\"runStatus\":\"Success\"}}";
        var signature = System.Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        using var unsigned = await client.PostAsync("/api/webhooks/dbt-cloud", new StringContent(payload, Encoding.UTF8, "application/json"));
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/dbt-cloud")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", signature);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("42");
    }
}
