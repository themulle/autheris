namespace Autheris.Tests.Integration;

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// The dataset tools are reachable through the real MCP endpoint (DI, tool registry, guardrail and executor).
/// </summary>
public class McpDatasetToolsIntegrationTests : IClassFixture<McpDatasetToolsIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpDatasetToolsIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("McpDatasetToolsIntegrationTests"));
        }
    }

    private static async Task<(bool IsError, JsonElement Payload)> CallAsync(HttpClient client, string tool, object arguments)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        return await McpTestClient.CallJsonAsync(mcp, tool, arguments);
    }

    [Fact]
    public async Task ToolsList_OffersTheDatasetTools()
    {
        var client = _factory.CreateClient();

        await using var mcp = await McpTestClient.ConnectAsync(client);
        var tools = await mcp.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        names.ShouldContain("list_datasets");
        names.ShouldContain("describe_dataset");
        names.ShouldContain("sample_rows");
    }

    [Fact]
    public async Task ListAndDescribe_WorkEndToEnd()
    {
        var client = _factory.CreateClient();

        var (listError, list) = await CallAsync(client, "list_datasets", new { });
        listError.ShouldBeFalse(list.ToString());
        var datasets = list.GetProperty("datasets");
        datasets.GetArrayLength().ShouldBeGreaterThan(0);
        var id = datasets[0].GetProperty("id").GetString()!;
        id.Split('.').Length.ShouldBe(3);

        var (describeError, description) = await CallAsync(client, "describe_dataset", new { dataset = id });
        describeError.ShouldBeFalse(description.ToString());
        description.GetProperty("id").GetString().ShouldBe(id);
        description.GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(0);

        var (missingError, missing) = await CallAsync(client, "describe_dataset", new { dataset = "no.such.table" });
        missingError.ShouldBeTrue();
        missing.GetProperty("error").GetProperty("code").GetString().ShouldBe("NOT_FOUND");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Five_to_nine_char_justification_rejected()
    {
        var client = _factory.CreateClient();
        var (_, list) = await CallAsync(client, "list_datasets", new { });
        var datasets = list.GetProperty("datasets");
        var id = datasets[0].GetProperty("id").GetString()!;

        foreach (var shortJustif in new[] { "12345", "1234567", "123456789" })
        {
            var (isError, payload) = await CallAsync(client, "request_access", new { dataset = id, justification = shortJustif });
            isError.ShouldBeTrue($"Short justification '{shortJustif}' ({shortJustif.Length} chars) must be rejected.");
            payload.ToString().ShouldContain("error");
        }
    }
}
