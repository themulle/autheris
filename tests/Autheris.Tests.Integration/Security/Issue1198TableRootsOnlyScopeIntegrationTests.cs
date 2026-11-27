namespace Autheris.Tests.Integration.Security;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>
/// Tokens carrying a scope listed in <c>Gateway:GraphQL:TableRootsOnlyScopes</c> may only use catalog table roots plus __typename;
/// every other known root is answered with ROOT_FIELD_NOT_ALLOWED and HTTP 403, over HTTP and over MCP.
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~Issue1198TableRootsOnlyScopeIntegrationTests"
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue1198TableRootsOnlyScopeIntegrationTests : IClassFixture<Issue1198TableRootsOnlyScopeIntegrationTests.RestrictedFixture>
{
    private const string RestrictedScope = "Agent.Read";
    private const string Code = "ROOT_FIELD_NOT_ALLOWED";

    private readonly RestrictedFixture _fixture;

    public Issue1198TableRootsOnlyScopeIntegrationTests(RestrictedFixture fixture) => _fixture = fixture;

    public sealed class RestrictedFixture : RowFilterChannelParityTests.Fixture
    {
        protected override void ConfigureHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:GraphQL:TableRootsOnlyScopes:0", RestrictedScope);
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            builder.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
        }
    }

    private HttpClient Caller(string? scope = RestrictedScope, string? scopeArray = null)
    {
        _fixture.ExtraHeaders.Clear();
        _fixture.ExtraHeaders["X-Test-Tier"] = "Internal";
        if (scope is not null)
        {
            _fixture.ExtraHeaders["X-Test-Scope"] = scope;
        }

        if (scopeArray is not null)
        {
            _fixture.ExtraHeaders["X-Test-Scope-Array"] = scopeArray;
        }

        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, string query)
    {
        var r = await client.PostAsJsonAsync("/graphql", new { query });
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private static void ShouldBeRejected(HttpStatusCode status, string body, string field)
    {
        status.ShouldBe(HttpStatusCode.Forbidden, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("data", out var data).ShouldBeFalse(body);
        var error = doc.RootElement.GetProperty("errors")[0];
        error.GetProperty("extensions").GetProperty("code").GetString().ShouldBe(Code);
        error.GetProperty("message").GetString()!.ShouldContain($"'{field}'");
    }

    [Theory]
    [InlineData("{ table(domain: \"default\", name: \"air1\") { __typename } }", "table")]
    [InlineData("{ catalog { __typename } }", "catalog")]
    [InlineData("{ myAccess { __typename } }", "myAccess")]
    [InlineData("{ __schema { queryType { name } } }", "__schema")]
    [InlineData("mutation { __typename }", "mutation")]
    public async Task Graphql_restricted_scope_rejects_disallowed_root(string query, string field)
    {
        var (status, body) = await PostAsync(Caller(), query);
        ShouldBeRejected(status, body, field);
    }

    [Fact]
    public async Task Graphql_get_request_is_rejected_like_post()
    {
        var r = await Caller().GetAsync("/graphql?query=" + System.Uri.EscapeDataString("{ catalog { __typename } }"));
        ShouldBeRejected(r.StatusCode, await r.Content.ReadAsStringAsync(), "catalog");
    }

    [Fact]
    public async Task Graphql_array_scp_token_is_restricted()
    {
        var (status, body) = await PostAsync(Caller(scope: null, scopeArray: "openid;Agent.Read"), "{ catalog { __typename } }");
        ShouldBeRejected(status, body, "catalog");
    }

    [Fact]
    public async Task Graphql_restricted_scope_allows_list_aggregate_connection_mix()
    {
        var (status, body) = await PostAsync(Caller(),
            "{ default_main_air1(first: 2) { id } default_main_air1_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } default_main_air1Connection(first: 2) { edges { node { id } } } __typename }");
        status.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out _).ShouldBeFalse(body);
    }

    [Fact]
    public async Task Graphql_mixed_table_and_catalog_rejected_without_data()
    {
        var (status, body) = await PostAsync(Caller(), "{ default_main_air1(first: 2) { id } catalog { __typename } }");
        ShouldBeRejected(status, body, "catalog");
    }

    [Fact]
    public async Task Graphql_mutation_body_with_unknown_field_is_rejected_without_echo()
    {
        var (status, body) = await PostAsync(Caller(), "mutation { evilUnknownField }");
        ShouldBeRejected(status, body, "mutation");
        body.ShouldNotContain("evilUnknownField");
    }

    [Fact]
    public async Task Graphql_unknown_root_keeps_shielded_invalid_query()
    {
        var restricted = await PostAsync(Caller(), "{ unknownRootX { __typename } }");
        var unrestricted = await PostAsync(Caller(scope: null), "{ unknownRootX { __typename } }");
        restricted.Status.ShouldBe(unrestricted.Status);
        restricted.Body.ShouldBe(unrestricted.Body);
        restricted.Body.ShouldNotContain(Code);
    }

    [Fact]
    public async Task Graphql_unrestricted_scope_unaffected()
    {
        var (status, body) = await PostAsync(Caller(scope: "some.other.scope"), "{ catalog { __typename } }");
        body.ShouldNotContain(Code);
        status.ShouldNotBe(HttpStatusCode.Forbidden, body);
    }

    [Theory]
    [InlineData(null, "openid;Agent.Read")]
    [InlineData(RestrictedScope, null)]
    public async Task Mcp_query_graphql_restricted_scope_rejects_catalog_and_allows_table_roots(string? scope, string? scopeArray)
    {
        var client = Caller(scope, scopeArray);
        await using var mcp = await McpTestClient.ConnectAsync(client);

        // The MCP pre-flight cannot map a non-table root to a table and denies it fail-closed before GraphQL runs,
        // so a restricted token never receives data for a catalog root over MCP.
        var (rejected, rejectedText) = await McpTestClient.CallAsync(mcp, "query_graphql",
            new { query = "{ default_main_air1(first: 2) { id } catalog { __typename } }" });
        rejected.ShouldBeTrue(rejectedText);
        rejectedText.ShouldNotContain("\"data\"");

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = "{ default_main_air1(first: 2) { id } }" });
        isError.ShouldBeFalse(payload.ToString());
    }
}
