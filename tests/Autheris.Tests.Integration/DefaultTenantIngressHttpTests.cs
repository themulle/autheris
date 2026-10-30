namespace Autheris.Tests.Integration;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// WP-DT-7 / DT-SC-01, DT-SC-09: tenant ingress at HTTP level (Docker-free). A principal without tenant claim is assigned the
/// configured default tenant (D); without D every endpoint class refuses it with 403 TENANT_UNRESOLVED; anonymous callers
/// never reach protected endpoints.
/// </summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantIngressHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DefaultTenant = "dflt-ingress";
    private readonly WebApplicationFactory<Program> _factory;

    public DefaultTenantIngressHttpTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> Host(string? defaultTenantId) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-DtIngress-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            b.UseSetting("Gateway:Rebac:Enabled", "true");
            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:Arrow:Enabled", "true");
            b.UseSetting("Gateway:Arrow:EnableFlightSql", "true");
            b.UseSetting("Gateway:Lakehouse:Enabled", "true");
            if (defaultTenantId is not null)
            {
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", defaultTenantId);
            }
            else
            {
                // appsettings.Development.json configures D; an empty value is rejected, so the key is nulled.
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = null }));
            }
        });

    public static TheoryData<string, string, string?> ClaimlessEndpoints => new()
    {
        { "GET", "/odata/v4", null },
        { "GET", "/odata/v4/$metadata", null },
        { "POST", "/graphql", """{"query":"{ catalog(first: 3) { tableName } }"}""" },
        { "POST", "/api/v1/sql", """{"sql":"SELECT 1"}""" },
        { "GET", "/api/v1/governance/audit", null },
        { "GET", "/api/v1/governance/grants", null },
        { "POST", "/mcp", """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_datasets","arguments":{}}}""" },
        { "GET", "/api/v1/rebac/tuples", null },
        { "POST", "/api/v1/consents/bulk", """{"subject":"user:a","tables":[]}""" },
        { "GET", "/scim/v2/dev/Users", null },
        { "GET", "/api/v1/flight/sql/tables", null },
        { "GET", "/v1/p/namespaces", null },
    };

    private static HttpRequestMessage Request(string method, string path, string? body, bool authenticated)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("GraphQL-Preflight", "1");
        if (authenticated)
        {
            request.Headers.Add("X-Test-User-Sid", "S-1-5-21-DT7");
            request.Headers.Add("X-Test-Roles", "GovernanceAdmin");
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(ClaimlessEndpoints))]
    public async Task DT_SC_01_Claimless_Principal_Without_Default_Tenant_Is_Refused_With_TenantUnresolved(string method, string path, string? body)
    {
        await using var host = Host(null);
        var client = host.CreateClient();

        var response = await client.SendAsync(Request(method, path, body, authenticated: true));
        var text = await response.Content.ReadAsStringAsync();

        // The middleware tenant gate refuses before any endpoint code runs: every endpoint class answers the same 403.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}: {Truncate(text)}");
        text.ShouldContain("TENANT_UNRESOLVED");
        text.ShouldNotContain(DefaultTenant);
    }

    [Theory]
    [MemberData(nameof(ClaimlessEndpoints))]
    public async Task DT_SC_01_Claimless_Principal_With_Default_Tenant_Is_Not_Refused_As_Unresolved(string method, string path, string? body)
    {
        await using var host = Host(DefaultTenant);
        var client = host.CreateClient();

        var response = await client.SendAsync(Request(method, path, body, authenticated: true));
        var text = await response.Content.ReadAsStringAsync();

        text.ShouldNotContain("TENANT_UNRESOLVED", customMessage: $"{method} {path} -> {(int)response.StatusCode}: {Truncate(text)}");
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, $"{method} {path}: {Truncate(text)}");
    }

    [Fact]
    public async Task DT_SC_01_Rebac_Works_For_The_Default_Tenant_Of_A_Claimless_Principal()
    {
        await using var host = Host(DefaultTenant);
        using (var scope = host.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<Autheris.Application.Security.Rebac.Interfaces.IRebacStore>();
            await store.AddTuplesAsync(new[] { new Autheris.Domain.Model.RebacTuple(DefaultTenant, "S-1-5-21-DT7-ALICE", "viewer", "table:sales.public.dt7_orders") }, CancellationToken.None);
            await store.AddTuplesAsync(new[] { new Autheris.Domain.Model.RebacTuple("other-tenant", "S-1-5-21-DT7-BOB", "viewer", "table:sales.public.dt7_invoices") }, CancellationToken.None);
        }

        var client = host.CreateClient();
        var request = Request("GET", "/api/v1/rebac/tuples", null, authenticated: true);
        request.Headers.Remove("X-Test-Roles");
        request.Headers.Add("X-Test-Roles", "ClusterAdmin");
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, Truncate(text));
        text.ShouldContain("S-1-5-21-DT7-ALICE");
        text.ShouldNotContain("S-1-5-21-DT7-BOB");
    }

    [Theory]
    [InlineData("/api/v1/governance/audit")]
    [InlineData("/api/v1/rebac/tuples")]
    [InlineData("/graphql")]
    [InlineData("/mcp")]
    [InlineData("/odata/v4/dom/sch/tbl")]
    [InlineData("/api/v1/sql")]
    public async Task DT_SC_09_Anonymous_Is_Unauthorized_On_Protected_Endpoints(string path)
    {
        foreach (var defaultTenant in new string?[] { null, DefaultTenant })
        {
            await using var host = Host(defaultTenant);
            var client = host.CreateClient();

            var response = await client.SendAsync(Request("GET", path, null, authenticated: false));

            // GraphQL and MCP answer GET with a transport-specific status; none of them may be a success.
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotAcceptable);
        }
    }

    [Theory]
    [InlineData("/graphql", """{"query":"{ __typename }"}""")]
    [InlineData("/mcp", """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")]
    [InlineData("/api/v1/sql", """{"sql":"SELECT 1"}""")]
    public async Task DT_SC_09_Anonymous_Post_Is_Unauthorized(string path, string body)
    {
        await using var host = Host(DefaultTenant);
        var client = host.CreateClient();

        var response = await client.SendAsync(Request("POST", path, body, authenticated: false));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_Is_Reachable_Anonymously_In_Both_Configurations(string path)
    {
        foreach (var defaultTenant in new string?[] { null, DefaultTenant })
        {
            await using var host = Host(defaultTenant);
            var response = await host.CreateClient().GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
