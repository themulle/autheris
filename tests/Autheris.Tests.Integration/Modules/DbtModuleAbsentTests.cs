namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>Host without module <c>dbt</c>: every dbt route is gone, core metric and GraphQL paths still work.</summary>
[Trait("Category", "Security")]
public sealed class DbtModuleAbsentTests : IClassFixture<DbtModuleAbsentTests.HostFixture>
{
    private const string Payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";

    private readonly WebApplicationFactory<Program> _factory;

    public DbtModuleAbsentTests(HostFixture factory) => _factory = factory;

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "DbtAbsent");
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", "tenant-1");
        }
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-User-Sid", "S-1-5-21-DBT-CONTRACT");
        request.Headers.Add("X-Test-Roles", "GovernanceAdmin");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-1");
        return request;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dbt_routes_404_when_absent()
    {
        using var client = _factory.CreateClient();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/modules/dbt/sync"),
            (HttpMethod.Post, "/modules/dbt/governance"),
            (HttpMethod.Get, "/modules/dbt/exposures"),
            (HttpMethod.Get, "/modules/dbt/proposals"),
            (HttpMethod.Post, "/modules/dbt/proposals/6f9619ff-8b86-d011-b42d-00c04fc964ff/approve"),
            (HttpMethod.Post, "/modules/dbt/proposals/6f9619ff-8b86-d011-b42d-00c04fc964ff/reject"),
            (HttpMethod.Post, "/modules/dbt/validate-contract"),
            (HttpMethod.Post, "/modules/dbt/run-results"),
            (HttpMethod.Get, "/modules/dbt/health"),
            (HttpMethod.Post, "/modules/dbt/health/reset"),
            (HttpMethod.Post, "/modules/dbt/semantic-manifest"),
            (HttpMethod.Get, "/modules/dbt/metrics"),
            // Former extension paths never come back.
            (HttpMethod.Post, "/api/extensions/dbt/sync"),
            (HttpMethod.Post, "/api/extensions/dbt/governance"),
            (HttpMethod.Get, "/api/extensions/dbt/exposures"),
            (HttpMethod.Get, "/api/extensions/dbt/proposals"),
            (HttpMethod.Post, "/api/extensions/dbt/proposals/6f9619ff-8b86-d011-b42d-00c04fc964ff/approve"),
            (HttpMethod.Post, "/api/extensions/dbt/proposals/6f9619ff-8b86-d011-b42d-00c04fc964ff/reject"),
            (HttpMethod.Post, "/api/extensions/dbt/validate-contract"),
            (HttpMethod.Post, "/api/extensions/dbt/run-results"),
            (HttpMethod.Get, "/api/extensions/dbt/health"),
            (HttpMethod.Post, "/api/extensions/dbt/health/reset"),
            (HttpMethod.Post, "/api/extensions/dbt/semantic-manifest"),
            (HttpMethod.Get, "/api/extensions/dbt/metrics"),
            (HttpMethod.Post, "/api/extensions/dbt/webhooks/dbt-cloud"),
        };

        foreach (var (method, path) in routes)
        {
            using var request = Authenticated(method, path);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Expected 404 when the dbt module is not enabled for {method} {path}.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_config_without_module_fails_fast()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "DbtConfigWithoutModule");
            builder.UseSetting("Gateway:Dbt:WebhookSecret", "configured-without-module");
        });

        var ex = Should.Throw<System.Exception>(() => factory.CreateClient());
        var text = ex.ToString();
        text.ShouldContain("Gateway:Dbt");
        text.ShouldContain("Autheris.Modules.Dbt");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dbt_cloud_webhook_returns_404_without_module_before_body_read()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/dbt-cloud")
        {
            Content = new StringContent(Payload, Encoding.UTF8, "application/json")
        };
        // A body above the 2 MB cap would answer 413 if the body were read before the module check.
        request.Headers.Add("X-Dbt-Signature", new string('a', 64));

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/dbt-cloud")
        {
            Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 4096])
        };
        oversized.Headers.Add("X-Dbt-Signature", new string('a', 64));
        using var oversizedResponse = await client.SendAsync(oversized);
        oversizedResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Analytics_metrics_work_without_dbt_module()
    {
        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Get, "/api/v1/analytics/metrics");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GraphQL_queries_work_without_dbt_module()
    {
        using var client = _factory.CreateClient();
        using var request = Authenticated(HttpMethod.Post, "/graphql");
        request.Headers.Add("GraphQL-Preflight", "1");
        request.Content = new StringContent("{\"query\":\"{ __typename }\"}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Query");
    }
}
