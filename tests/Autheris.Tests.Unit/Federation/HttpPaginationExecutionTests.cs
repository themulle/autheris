namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class HttpPaginationExecutionTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IKeyVaultSecretProvider _secretProvider = Substitute.For<IKeyVaultSecretProvider>();
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();

    public HttpPaginationExecutionTests()
    {
        _environment.EnvironmentName.Returns("Development");
    }

    private static DataSourceExecutionContext CreateContext(HttpEndpointDescriptor endpoint)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new("tenant_id", "tenant-1")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var table = new Table
        {
            SourceName = "crm",
            SchemaName = "dbo",
            TableName = "customers",
            DataSourceType = DataSourceType.HttpDeclarative,
            HttpEndpoint = endpoint
        };

        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "dbo", "customers"),
            Table = table
        };

        var decision = TableAccessDecision.Allowed(
            metadata.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: true);

        return new DataSourceExecutionContext(
            SourceName: "crm",
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "name"]);
    }

    [Fact]
    public async Task ExecutePaged_OffsetLimit_FetchesAllPagesUntilExhausted()
    {
        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/items",
            JsonRootPath = "items",
            Pagination = new HttpPaginationConfig
            {
                Strategy = HttpPaginationStrategy.OffsetLimit,
                PageParamName = "offset",
                SizeParamName = "limit",
                DefaultPageSize = 2,
                MaxPages = 10
            }
        };

        var context = CreateContext(endpoint);

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("offset=0"))
            {
                var json = "{\"items\": [{\"id\": 1}, {\"id\": 2}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            if (uri.Contains("offset=2"))
            {
                var json = "{\"items\": [{\"id\": 3}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\": []}") };
        });

        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var executor = new DeclarativeHttpDataSourceExecutor(
            _httpClientFactory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, _secretProvider, _environment);

        var results = await executor.ExecutePagedRequestsAsync(endpoint, context);

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ExecutePaged_NextLink_FollowsLinksCorrectly()
    {
        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/users",
            JsonRootPath = "value",
            Pagination = new HttpPaginationConfig
            {
                Strategy = HttpPaginationStrategy.NextLinkUrl,
                NextLinkJsonPath = "@odata.nextLink",
                MaxPages = 5
            }
        };

        var context = CreateContext(endpoint);

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (!uri.Contains("page=2"))
            {
                var json = "{\"@odata.nextLink\": \"https://api.example.com/users?page=2\", \"value\": [{\"id\": 101}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            else
            {
                var json = "{\"value\": [{\"id\": 102}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
        });

        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var executor = new DeclarativeHttpDataSourceExecutor(
            _httpClientFactory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, _secretProvider, _environment);

        var results = await executor.ExecutePagedRequestsAsync(endpoint, context);

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecutePaged_NextLinkPointingToExternalDomain_ThrowsSecurityException()
    {
        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/users",
            JsonRootPath = "value",
            Pagination = new HttpPaginationConfig
            {
                Strategy = HttpPaginationStrategy.NextLinkUrl,
                NextLinkJsonPath = "@odata.nextLink",
                EnforceSameHost = true
            }
        };

        var context = CreateContext(endpoint);

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var json = "{\"@odata.nextLink\": \"https://evil-attacker.com/leak\", \"value\": [{\"id\": 1}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var executor = new DeclarativeHttpDataSourceExecutor(
            _httpClientFactory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, _secretProvider, _environment);

        await Should.ThrowAsync<SecurityException>(() => executor.ExecutePagedRequestsAsync(endpoint, context));
    }

    [Fact]
    public async Task ExecutePaged_ExceedsMaxPages_StopsAtConfiguredLimit()
    {
        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/infinite",
            JsonRootPath = "items",
            Pagination = new HttpPaginationConfig
            {
                Strategy = HttpPaginationStrategy.PageNumber,
                DefaultPageSize = 1,
                MaxPages = 3
            }
        };

        var context = CreateContext(endpoint);

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var json = "{\"items\": [{\"val\": 42}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var executor = new DeclarativeHttpDataSourceExecutor(
            _httpClientFactory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, _secretProvider, _environment);

        var results = await executor.ExecutePagedRequestsAsync(endpoint, context);

        // Should stop at MaxPages = 3
        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ExecutePaged_PayloadExceedsByteLimit_AbortsWithBudgetExceeded()
    {
        var endpoint = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.example.com",
            PathTemplate = "/huge",
            JsonRootPath = "items",
            Pagination = new HttpPaginationConfig
            {
                Strategy = HttpPaginationStrategy.PageNumber,
                MaxStagedBytes = 50 // Tiny quota for test
            }
        };

        var context = CreateContext(endpoint);

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var json = "{\"items\": [{\"id\": 1, \"data\": \"this string is definitely longer than 50 bytes in json payload\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var executor = new DeclarativeHttpDataSourceExecutor(
            _httpClientFactory, NullLogger<DeclarativeHttpDataSourceExecutor>.Instance, _secretProvider, _environment);

        await Should.ThrowAsync<ConnectorRowLimitExceededException>(() => executor.ExecutePagedRequestsAsync(endpoint, context));
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
