namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpResourceProviderTests
{
    private readonly ICatalogDiscoveryService _discovery = Substitute.For<ICatalogDiscoveryService>();
    private readonly IApiDispatcherService _dispatcher = Substitute.For<IApiDispatcherService>();
    private readonly McpResourceProvider _provider;

    private static readonly McpSessionContext Session = new(
        "s-1", "sp-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        UserSid: "S-1-5-21-1", Roles: ["Analyst", "GovernanceAdmin"], GroupSids: ["S-1-5-32-1"], IsReadOnly: true);

    public McpResourceProviderTests()
    {
        _provider = new McpResourceProvider(_discovery, _dispatcher);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [InlineData("autheris://catalog/summary", true)]
    [InlineData("autheris://governance/my-access", true)]
    [InlineData("autheris://api/openapi.json", true)]
    [InlineData("autheris://datasets/sales.public.orders", false)]
    public void IsNativeResource_MatchesNativeSchemes(string uri, bool expected)
        => _provider.IsNativeResource(uri).ShouldBe(expected);

    [Fact]
    public async Task ReadCatalogSummary_RendersMarkdownGolden_AndBuildsCallerContext()
    {
        RequestContext? seen = null;
        _discovery.ListDatasetsAsync(Arg.Do<RequestContext>(c => seen = c), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasetSummary("sales.public.orders", "sales", "public", "orders", "Sql", "Internal", null, true)]);

        var result = await _provider.ReadAsync("autheris://catalog/summary", Session);

        result.MimeType.ShouldBe("text/markdown");
        Normalize(result.Text).ShouldBe(
            "# Autheris Data Catalog Summary\n\n" +
            "| Dataset | Domain | Schema | Table | Type | Sensitivity | Description |\n" +
            "|---|---|---|---|---|---|---|\n" +
            "| `sales.public.orders` | sales | public | orders | Sql | Internal | - |\n");
        seen.ShouldNotBeNull();
        seen!.TenantId.ShouldBe(new TenantId("tenant-1"));
    }

    [Fact]
    public async Task ReadDatasources_RendersMarkdownGolden()
    {
        _discovery.ListDatasourcesAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasourceSummary("ds-1", "SalesDW", "sales", "SqlServer", null, true, "healthy", DateTimeOffset.UtcNow)]);

        var result = await _provider.ReadAsync("autheris://catalog/datasources", Session);

        Normalize(result.Text).ShouldBe(
            "# Registered Enterprise Datasources\n\n" +
            "| Datasource ID | Name | Domain | Type | Status | Configured |\n" +
            "|---|---|---|---|---|---|\n" +
            "| `ds-1` | SalesDW | sales | SqlServer | healthy | ✓ |\n");
    }

    [Fact]
    public async Task ReadDatasetSchema_RendersMarkdownGolden()
    {
        _discovery.GetDatasetDetailAsync(Arg.Any<TableIdentifier>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogDatasetDetail(
                "sales.public.orders", "sales", "public", "orders",
                [new CatalogColumnDetail("id", "int", "Public", "clear", true, false), new CatalogColumnDetail("email", "text", "Pii", "mask", false, true)],
                ["id"], "Internal", true));

        var result = await _provider.ReadAsync("autheris://catalog/datasets/sales.public.orders/schema", Session);

        Normalize(result.Text).ShouldBe(
            "# Schema for `sales.public.orders`\n\n" +
            "- **Domain:** sales\n" +
            "- **Table:** orders\n" +
            "- **Sensitivity:** Internal\n" +
            "- **Active:** True\n\n" +
            "| Column | Type | Sensitivity | Masking | PK | PII |\n" +
            "|---|---|---|---|---|---|\n" +
            "| `id` | int | Public | clear | ✓ | - |\n" +
            "| `email` | text | Pii | mask | - | ✓ |\n");
    }

    [Theory]
    [InlineData("autheris://catalog/datasets/not-a-dataset/schema")]
    [InlineData("autheris://catalog/unknown")]
    [InlineData("autheris://api/unknown")]
    public async Task Read_UnknownOrInvalidResource_ThrowsNotFound(string uri)
        => await Should.ThrowAsync<McpResourceNotFoundException>(() => _provider.ReadAsync(uri, Session));

    [Fact]
    public async Task ReadDatasetSchema_WhenDetailMissing_ThrowsNotFound()
    {
        _discovery.GetDatasetDetailAsync(Arg.Any<TableIdentifier>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns((CatalogDatasetDetail?)null);

        await Should.ThrowAsync<McpResourceNotFoundException>(() =>
            _provider.ReadAsync("autheris://catalog/datasets/sales.public.orders/schema", Session));
    }

    [Fact]
    public async Task ReadMyAccess_RendersSessionProfileGolden()
    {
        var result = await _provider.ReadAsync("autheris://governance/my-access", Session);

        Normalize(result.Text).ShouldBe(
            "# Autheris Governance - Current Session Access Profile\n\n" +
            "- **Principal:** `S-1-5-21-1`\n" +
            "- **Tenant:** `tenant-1`\n" +
            "- **Roles:** Analyst, GovernanceAdmin\n" +
            "- **Groups:** S-1-5-32-1\n" +
            "- **Read-Only:** True\n");
    }

    [Fact]
    public async Task ReadApiResources_ReturnDispatcherContentWithMimeType()
    {
        _dispatcher.GetOpenApiJsonAsync(Arg.Any<CancellationToken>()).Returns("{\"openapi\":\"3.1.0\"}");
        _dispatcher.GetEndpointsDocumentationAsync(Arg.Any<CancellationToken>()).Returns("# Endpoints");
        _dispatcher.GetMcpToolsDocumentationAsync(Arg.Any<CancellationToken>()).Returns("# Tools");

        var openApi = await _provider.ReadAsync("autheris://api/openapi.json", Session);
        var endpoints = await _provider.ReadAsync("autheris://api/docs/endpoints", Session);
        var tools = await _provider.ReadAsync("autheris://api/docs/mcp-tools", Session);

        openApi.ShouldBe(new McpResourceContent("autheris://api/openapi.json", "application/json", "{\"openapi\":\"3.1.0\"}"));
        endpoints.ShouldBe(new McpResourceContent("autheris://api/docs/endpoints", "text/markdown", "# Endpoints"));
        tools.ShouldBe(new McpResourceContent("autheris://api/docs/mcp-tools", "text/markdown", "# Tools"));
    }
}
