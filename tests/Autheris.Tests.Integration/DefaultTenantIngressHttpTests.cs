namespace Autheris.Tests.Integration;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// WP-DT-7 / DT-SC-01, DT-SC-09: tenant ingress at HTTP level (Docker-free). A principal without tenant claim is assigned the
/// configured default tenant (D); without D every endpoint class refuses it with 403 TENANT_UNRESOLVED; anonymous callers
/// never reach protected endpoints.
/// </summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantIngressHttpTests : IClassFixture<DefaultTenantIngressHttpTests.Fixture>
{
    private const string DefaultTenant = "dflt-ingress";
    private readonly Fixture _fixture;

    public DefaultTenantIngressHttpTests(Fixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> Host(string? defaultTenantId) => _fixture.Host(defaultTenantId);

    public static TheoryData<string, string, string?> ClaimlessEndpoints => new()
    {
        { "GET", "/odata/v4", null },
        { "GET", "/odata/v4/$metadata", null },
        { "POST", "/graphql", """{"query":"{ catalog(first: 3) { tableName } }"}""" },
        { "POST", "/api/v1/sql", """{"sql":"SELECT 1"}""" },
        { "GET", "/api/v1/governance/audit", null },
        { "GET", "/api/v1/governance/grants", null },
        { "POST", "/mcp", """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_datasets","arguments":{}}}""" },
        { "POST", "/api/v1/consents/bulk", """{"subject":"user:a","tables":[]}""" },
        { "GET", "/modules/lakehouse/iceberg/v1/p/namespaces", null },
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
        var host = Host(null);
        using var client = host.CreateClient();

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
        var host = Host(DefaultTenant);
        using var client = host.CreateClient();

        var response = await client.SendAsync(Request(method, path, body, authenticated: true));
        var text = await response.Content.ReadAsStringAsync();

        text.ShouldNotContain("TENANT_UNRESOLVED", customMessage: $"{method} {path} -> {(int)response.StatusCode}: {Truncate(text)}");
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, $"{method} {path}: {Truncate(text)}");
    }

    [Theory]
    [InlineData("/api/v1/governance/audit")]
    [InlineData("/graphql")]
    [InlineData("/mcp")]
    [InlineData("/odata/v4/dom/sch/tbl")]
    [InlineData("/api/v1/sql")]
    public async Task DT_SC_09_Anonymous_Is_Unauthorized_On_Protected_Endpoints(string path)
    {
        foreach (var defaultTenant in new string?[] { null, DefaultTenant })
        {
            var host = Host(defaultTenant);
            using var client = host.CreateClient();

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
        var host = Host(DefaultTenant);
        using var client = host.CreateClient();

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
            var host = Host(defaultTenant);
            using var client = host.CreateClient();
            var response = await client.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    public sealed class Fixture : IDisposable
    {
        private readonly IntegrationHost _withoutDefaultTenant;
        private readonly IntegrationHost _withDefaultTenant;

        public Fixture()
        {
            _withoutDefaultTenant = CreateHost(null);
            _withDefaultTenant = CreateHost(DefaultTenant);
        }

        public WebApplicationFactory<Program> Host(string? defaultTenantId) =>
            defaultTenantId is null ? _withoutDefaultTenant : _withDefaultTenant;

        private static IntegrationHost CreateHost(string? defaultTenantId)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "1000",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("Fixture"),
                ["Gateway:Mcp:Enabled"] = "true",
                ["Gateway:Mcp:EndpointPath"] = "/mcp",
                ["Gateway:WebSql:Enabled"] = "true",
                ["Gateway:Arrow:Enabled"] = "true",
                ["Gateway:Modules:Enabled:0"] = "lakehouse",
                ["Gateway:Lakehouse:Enabled"] = "true",
                // appsettings.Development.json configures D; an empty value is rejected, so the key is nulled when omitted.
                ["Gateway:Tenancy:DefaultTenantId"] = defaultTenantId,
            };
            LakehouseWorkspaceTestSettings.Apply((k, v) => settings[k] = v);
            return new IntegrationHost(settings);
        }

        public void Dispose()
        {
            _withoutDefaultTenant.Dispose();
            _withDefaultTenant.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
