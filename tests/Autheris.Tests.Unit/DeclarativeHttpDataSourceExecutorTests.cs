using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class DeclarativeHttpDataSourceExecutorTests
{
    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private static (DeclarativeHttpDataSourceExecutor Executor, List<HttpRequestMessage> Requests) CreateExecutor(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        bool isDev = true)
    {
        var requests = new List<HttpRequestMessage>();
        var lockObj = new object();
        var mockHandler = new DelegatingMockHandler(req =>
        {
            lock (lockObj)
            {
                requests.Add(req);
            }
            return handler(req);
        });

        var httpClient = new HttpClient(mockHandler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns(isDev ? "Development" : "Production");

        var executor = new DeclarativeHttpDataSourceExecutor(
            factory,
            NullLogger<DeclarativeHttpDataSourceExecutor>.Instance,
            environment: env);

        return (executor, requests);
    }

    private static TableMetadata CreateTestMetadata(HttpEndpointDescriptor? descriptor)
    {
        var id = new TableIdentifier("crm", "public", "customers");
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "crm",
                SchemaName = "public",
                TableName = "customers",
                DataSourceType = DataSourceType.HttpDeclarative,
                HttpEndpoint = descriptor
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };
    }

    private static DataSourceExecutionContext CreateContext(
        TableMetadata metadata,
        ClaimsPrincipal? principal = null,
        IReadOnlyDictionary<string, object?>? arguments = null,
        IReadOnlyDictionary<string, string[]>? requestHeaders = null)
    {
        var userSid = new Sid("S-1-5-21-1");
        principal ??= new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, userSid.Value)], "Test"));
        arguments ??= new Dictionary<string, object?>();

        var decision = TableAccessDecision.Allowed(
            metadata.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            null,
            hasUnconstrainedColumnAllow: true);

        return new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: arguments,
            RequestedFields: ["id", "name"],
            RequestHeaders: requestHeaders
        );
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsInvalidOperationException_WhenHttpEndpointDescriptorIsNull()
    {
        var (executor, _) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var metadata = CreateTestMetadata(null);
        var context = CreateContext(metadata);

        await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteAsync(context));
    }

    [Fact]
    public async Task ExecuteAsync_ExpandsUrlTemplatePlaceholders_AndAppendsQueryParams()
    {
        var (executor, requests) = CreateExecutor(req =>
        {
            var json = JsonSerializer.Serialize(new[]
            {
                new { id = 42, name = "Alice" }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/tenants/{tenant}/customers/{id}",
            Method = "GET"
        };
        var metadata = CreateTestMetadata(descriptor);

        var context = CreateContext(
            metadata,
            arguments: new Dictionary<string, object?>
            {
                ["tenant"] = "emea-1",
                ["id"] = 42,
                ["status"] = "active"
            });

        var rows = await executor.ExecuteAsync(context);

        requests.Count.ShouldBe(1);
        var req = requests[0];
        req.Method.ShouldBe(HttpMethod.Get);
        req.RequestUri.ShouldNotBeNull();
        req.RequestUri.ToString().ShouldBe("https://api.crm.example.com/v1/tenants/emea-1/customers/42?status=active");

        rows.Count.ShouldBe(1);
        rows[0]["id"]?.ToString().ShouldBe("42");
        rows[0]["name"]?.ToString().ShouldBe("Alice");
    }

    [Fact]
    public async Task ExecuteAsync_PushesDownTenantAndUserIdentityHeaders()
    {
        var (executor, requests) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers",
            TenantIdHeaderName = "X-Tenant-ID"
        };
        var metadata = CreateTestMetadata(descriptor);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-999"),
            new Claim("tenant_id", "tenant-xyz")
        ], "Test"));

        var context = CreateContext(metadata, principal: principal);

        await executor.ExecuteAsync(context);

        requests.Count.ShouldBe(1);
        var req = requests[0];
        req.Headers.Contains("X-Tenant-ID").ShouldBeTrue();
        req.Headers.GetValues("X-Tenant-ID").First().ShouldBe("tenant-xyz");
        req.Headers.Contains("X-User-Sid").ShouldBeTrue();
        req.Headers.GetValues("X-User-Sid").First().ShouldBe("S-1-5-21-999");
    }

    [Fact]
    public async Task ExecuteAsync_PushesDownTenantIdQueryParam()
    {
        var (executor, requests) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers",
            TenantIdQueryParam = "tid"
        };
        var metadata = CreateTestMetadata(descriptor);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-999"),
            new Claim("tenant_id", "tenant-456")
        ], "Test"));

        var context = CreateContext(metadata, principal: principal);

        await executor.ExecuteAsync(context);

        requests.Count.ShouldBe(1);
        requests[0].RequestUri!.Query.ShouldContain("tid=tenant-456");
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsBearerToken_WhenAuthModeIsForwardBearerToken()
    {
        var (executor, requests) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers",
            AuthMode = HttpAuthMode.ForwardBearerToken
        };
        var metadata = CreateTestMetadata(descriptor);

        var headers = new Dictionary<string, string[]>
        {
            ["Authorization"] = ["Bearer token-abc-123"]
        };

        var context = CreateContext(metadata, requestHeaders: headers);

        await executor.ExecuteAsync(context);

        requests.Count.ShouldBe(1);
        requests[0].Headers.Authorization.ShouldNotBeNull();
        requests[0].Headers.Authorization!.Scheme.ShouldBe("Bearer");
        requests[0].Headers.Authorization!.Parameter.ShouldBe("token-abc-123");
    }

    [Fact]
    public async Task ExecuteAsync_AppliesApiKey_WhenAuthModeIsStaticApiKey()
    {
        var (executor, requests) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers",
            AuthMode = HttpAuthMode.StaticApiKey,
            ApiKeyHeaderName = "X-Api-Key",
            ApiKeySecretName = "secret-key-999"
        };
        var metadata = CreateTestMetadata(descriptor);

        var context = CreateContext(metadata);

        await executor.ExecuteAsync(context);

        requests.Count.ShouldBe(1);
        requests[0].Headers.Contains("X-Api-Key").ShouldBeTrue();
        requests[0].Headers.GetValues("X-Api-Key").First().ShouldBe("secret-key-999");
    }

    [Fact]
    public void ExtractRowsFromJson_ExtractsNestedArray_AccordingToJsonRootPath()
    {
        var json = """
        {
            "status": 200,
            "data": {
                "items": [
                    { "id": 1, "name": "Alice", "score": 98.5, "active": true },
                    { "id": 2, "name": "Bob", "score": null, "active": false }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rows = DeclarativeHttpDataSourceExecutor.ExtractRowsFromJson(doc.RootElement, "data.items");

        rows.Count.ShouldBe(2);
        rows[0]["id"]?.ToString().ShouldBe("1");
        rows[0]["name"].ShouldBe("Alice");
        rows[0]["active"].ShouldBe(true);
        rows[1]["id"]?.ToString().ShouldBe("2");
        rows[1]["name"].ShouldBe("Bob");
        rows[1]["score"].ShouldBeNull();
        rows[1]["active"].ShouldBe(false);
    }

    [Fact]
    public void ExtractRowsFromJson_ReturnsSingleRow_WhenRootIsSingleObject()
    {
        var json = """
        {
            "id": 101,
            "title": "Report"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rows = DeclarativeHttpDataSourceExecutor.ExtractRowsFromJson(doc.RootElement, null);

        rows.Count.ShouldBe(1);
        rows[0]["id"]?.ToString().ShouldBe("101");
        rows[0]["title"].ShouldBe("Report");
    }

    [Fact]
    public void ExtractRowsFromJson_ReturnsEmpty_WhenJsonRootPathDoesNotExist()
    {
        var json = """{ "items": [] }""";
        using var doc = JsonDocument.Parse(json);
        var rows = DeclarativeHttpDataSourceExecutor.ExtractRowsFromJson(doc.RootElement, "non.existent.path");

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteBatchAsync_QueryParameterList_ProducesJoinedKeys()
    {
        var (executor, requests) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\": 1}, {\"id\": 2}]", Encoding.UTF8, "application/json")
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers",
            BatchType = HttpBatchType.QueryParameterList,
            BatchParamName = "ids"
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var rows = await executor.ExecuteBatchAsync(descriptor, context, ["1", "2", "3"]);

        requests.Count.ShouldBe(1);
        requests[0].RequestUri!.Query.ShouldContain("ids=1%2C2%2C3");
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteBatchAsync_JsonBodyArray_PostsJsonArray()
    {
        string? receivedBody = null;
        var (executor, requests) = CreateExecutor(req =>
        {
            if (req.Content != null)
            {
                receivedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\": 10}, {\"id\": 20}]", Encoding.UTF8, "application/json")
            };
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers/batch",
            BatchType = HttpBatchType.JsonBodyArray
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var rows = await executor.ExecuteBatchAsync(descriptor, context, ["10", "20"]);

        requests.Count.ShouldBe(1);
        requests[0].Method.ShouldBe(HttpMethod.Post);
        receivedBody.ShouldBe("[\"10\",\"20\"]");
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteBatchAsync_ParallelSingleRequests_ExecutesThrottledRequests()
    {
        var counter = 0;
        var (executor, requests) = CreateExecutor(_ =>
        {
            var id = Interlocked.Increment(ref counter);
            var json = JsonSerializer.Serialize(new[] { new { id, name = $"Customer_{id}" } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "https://api.crm.example.com",
            PathTemplate = "/v1/customers/{id}",
            BatchType = HttpBatchType.ParallelSingleRequests,
            PrimaryKeyField = "id",
            MaxConcurrentRequests = 2
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var rows = await executor.ExecuteBatchAsync(descriptor, context, ["1", "2", "3"]);

        requests.Count.ShouldBe(3);
        rows.Count.ShouldBe(3);
        rows.Select(r => r["id"]?.ToString()).ShouldBe(["1", "2", "3"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("https://127.0.0.1/api/data")]
    [InlineData("https://localhost/api/data")]
    [InlineData("https://[::1]/api/data")]
    [InlineData("https://10.0.1.5/api/data")]
    [InlineData("https://172.16.0.1/api/data")]
    [InlineData("https://172.31.255.255/api/data")]
    [InlineData("https://192.168.1.100/api/data")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[fe80::1]/api/data")]
    [InlineData("https://[fc00::1]/api/data")]
    [InlineData("https://[fd12:3456:789a::1]/api/data")]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenUrlTargetsPrivateOrLoopbackIp(string destinationUrl)
    {
        var (executor, _) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK), isDev: false);
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = destinationUrl,
            PathTemplate = "/"
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("strictly forbidden");
    }

    [Theory]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("https://kubernetes.default.svc/api/v1/")]
    [InlineData("https://kubernetes.default.svc.cluster.local/api/v1/")]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenUrlTargetsCloudMetadataOrKubernetes(string destinationUrl)
    {
        var (executor, _) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = destinationUrl,
            PathTemplate = "/"
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("strictly forbidden");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenInsecureHttpSchemeInNonDev()
    {
        var (executor, _) = CreateExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK), isDev: false);
        var descriptor = new HttpEndpointDescriptor
        {
            BaseUrl = "http://api.external.com",
            PathTemplate = "/v1/data"
        };
        var metadata = CreateTestMetadata(descriptor);
        var context = CreateContext(metadata);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Insecure HTTP scheme");
    }
}
