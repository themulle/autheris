namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

public class McpIntegrationTests : IClassFixture<McpIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Mcp:Enabled"] = "true",
                ["Gateway:Mcp:EndpointPath"] = "/mcp",
                ["Gateway:Insecure:danger_bypass_mcp_auth"] = "true",
                ["Gateway:Insecure:danger_allow_anonymous_access"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("McpIntegrationTests"),
            });
        }
    }

    [Fact]
    public async Task McpStreamableHttpFlow_ShouldWorkEndToEnd()
    {
        await using var client = await McpTestClient.ConnectAsync(_factory.CreateClient());

        client.ServerInfo.ShouldNotBeNull();
        client.ServerInfo.Name.ShouldBe("Autheris.McpServer");

        var (_, text) = await McpTestClient.CallAsync(client, "query_customers", new { });

        // SEC M-17: Der MCP-Executor liefert keine erfundenen Beispieldaten mehr (frueher "Erika Mustermann"),
        // sondern echte Daten oder ein strukturiertes Fehler-Ergebnis.
        text.ShouldNotBeNullOrWhiteSpace();
        text.ShouldNotContain("erika.mustermann@acme-corp.com");
        text.ShouldNotContain("Erika Mustermann");
        text.ShouldNotContain("Diabetes Type 2");
    }

    [Fact]
    public async Task McpResourcesFlow_AndLegacySimulateQueryForm_FailsClosed()
    {
        await using var client = await McpTestClient.ConnectAsync(_factory.CreateClient());

        // resources/list and resources/read
        var resources = await client.ListResourcesAsync();
        resources.ShouldNotBeEmpty();
        var read = await client.ReadResourceAsync(resources[0].Uri);
        read.Contents.ShouldNotBeEmpty();

        // The legacy table(...) form is not part of the gateway schema: simulate_query denies it as an unresolvable query.
        var (simulationError, simulation) = await McpTestClient.CallAsync(client, "simulate_query",
            new { query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 15) { id name } }" });
        simulationError.ShouldBeTrue(simulation);
        simulation.ShouldContain("DATASET_NOT_FOUND");

        // query_customers is a curated tool whose GraphQL operation the caller cannot read: the governed GraphQL errors come back
        // as EXECUTION_FAILED (filtered like /graphql), never mock data.
        var (customersError, customers) = await McpTestClient.CallAsync(client, "query_customers", new { });
        customersError.ShouldBeTrue(customers);
        customers.ShouldContain("\"code\":\"EXECUTION_FAILED\"");
        customers.ShouldNotContain("Erika Mustermann");
    }

    [Fact]
    public async Task H02_McpEndpoints_RequireAuthentication()
    {
        // SEC H-02: MCP (incl. tools/call) stays behind authentication unless the production-blocked
        // danger_bypass_mcp_auth switch is set.
        using var openFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "false");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "false");
        });

        var client = openFactory.CreateClient();

        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(initPayload, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var postResp = await client.SendAsync(request);
        postResp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task M09_McpStreamableHttp_OversizedBody_IsRejectedWith413()
    {
        var client = _factory.CreateClient();

        var hugeParam = new string('a', 1024 * 1024 + 16);
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + hugeParam + "\"}}";
        var response = await client.PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
