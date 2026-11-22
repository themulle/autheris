namespace Autheris.Tests.Unit.Connectors;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeclarativeHttpNextLinkTests
{
    private static DataSourceExecutionContext CreateContext(HttpEndpointDescriptor endpoint)
    {
        var id = new TableIdentifier("crm", "dbo", "items");
        var metadata = new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "crm",
                SchemaName = "dbo",
                TableName = "items",
                DataSourceType = DataSourceType.HttpDeclarative,
                HttpEndpoint = endpoint
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        return new DataSourceExecutionContext(
            SourceName: "crm",
            Metadata: metadata,
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "TestAuth")),
            AccessDecision: TableAccessDecision.Allowed(id, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id"]);
    }

    [Theory]
    [InlineData("/items?page=2", "https://api.example.com/items?page=2")]
    [InlineData("items?page=2", "https://api.example.com/v1/items?page=2")]
    [InlineData("?page=2", "https://api.example.com/v1/?page=2")]
    [InlineData("https://API.EXAMPLE.COM/items?page=2", "https://api.example.com/items?page=2")]
    public async Task ExecutePaged_NextLink_RelativeAndRootRelativeOnLinux_ResolvesAgainstBaseUri(
        string nextLink,
        string expectedSecondUrl)
    {
        var factory = new StubFactory(req =>
        {
            var url = req.RequestUri!.ToString();
            var json = !url.Contains("page=2", StringComparison.Ordinal)
                ? $$"""{"@odata.nextLink": "{{nextLink}}", "value": [{"id": 1}]}"""
                : """{"value": [{"id": 2}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com/v1/",
            PathTemplate = "/items",
            JsonRootPath = "value",
            Pagination = new HttpPaginationConfig(
                HttpPaginationStrategy.NextLinkUrl,
                NextLinkJsonPath: "@odata.nextLink",
                EnforceSameHost: true,
                MaxPages: 5)
        };

        var executor = new DeclarativeHttpDataSourceExecutor(
            factory,
            NullLogger<DeclarativeHttpDataSourceExecutor>.Instance,
            environment: new TestHostEnvironment("Development"));

        var rows = await executor.ExecutePagedRequestsAsync(endpoint, CreateContext(endpoint));

        rows.Count.ShouldBe(2);
        factory.RequestedUris.Count.ShouldBe(2);
        factory.RequestedUris[1].ToString().ShouldBe(expectedSecondUrl);
    }

    [Theory]
    [InlineData("file:///etc/passwd", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://api.example.com/items?page=2", true)]
    [InlineData("ftp://api.example.com/items?page=2", false)]
    [InlineData("javascript:alert(1)", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/plain,evil", true)]
    [InlineData("https://evil.example.com/items?page=2", true)]
    [InlineData("http://api.example.com/items?page=2", true)]
    [InlineData("https://api.example.com:8443/items?page=2", true)]
    [InlineData("//evil.example.com/items?page=2", true)]
    [InlineData("/\\evil.example.com/items?page=2", true)]
    public async Task ExecutePaged_NextLink_DisallowedSchemeOrCrossHost_ThrowsSecurityExceptionBeforeRequest(
        string maliciousNextLink,
        bool enforceSameHost)
    {
        var factory = new StubFactory(_ =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["@odata.nextLink"] = maliciousNextLink,
                ["value"] = new[] { new { id = 1 } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/items",
            JsonRootPath = "value",
            Pagination = new HttpPaginationConfig(
                HttpPaginationStrategy.NextLinkUrl,
                NextLinkJsonPath: "@odata.nextLink",
                EnforceSameHost: enforceSameHost,
                MaxPages: 5)
        };

        var executor = new DeclarativeHttpDataSourceExecutor(
            factory,
            NullLogger<DeclarativeHttpDataSourceExecutor>.Instance,
            environment: new TestHostEnvironment("Development"));

        await Should.ThrowAsync<SecurityException>(() =>
            executor.ExecutePagedRequestsAsync(endpoint, CreateContext(endpoint)));

        factory.RequestedUris.Count.ShouldBe(1);
    }

    [Fact]
    public void ResolveNextLinkUrl_RelativeWithoutBaseUri_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            DeclarativeHttpDataSourceExecutor.ResolveNextLinkUrl("/items?page=2", baseUri: null, enforceSameHost: true));
        Should.Throw<SecurityException>(() =>
            DeclarativeHttpDataSourceExecutor.ResolveNextLinkUrl("items?page=2", baseUri: null, enforceSameHost: false));
    }

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<Uri> RequestedUris { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, responder));

        private sealed class Handler(StubFactory owner, Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.RequestedUris.Add(request.RequestUri!);
                return Task.FromResult(responder(request));
            }
        }
    }
}
