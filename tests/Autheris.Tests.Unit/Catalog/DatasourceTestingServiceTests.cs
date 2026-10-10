namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DatasourceTestingServiceTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly IKeyVaultSecretProvider _secretProvider = Substitute.For<IKeyVaultSecretProvider>();
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();

    public DatasourceTestingServiceTests()
    {
        _environment.EnvironmentName.Returns("Development");
    }

    private static ClaimsPrincipal CreateAdminUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-user"),
            new(ClaimTypes.Role, "Admin"),
            new("tenant_id", "tenant-test")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private TableMetadata CreateHttpTable(string domain, string tableName, string baseUrl, string? pathTemplate = null)
    {
        var endpoint = new HttpEndpointDescriptor
        {
            Name = $"{domain} endpoint",
            BaseUrl = baseUrl,
            PathTemplate = pathTemplate ?? "/api/items",
            AuthMode = HttpAuthMode.StaticApiKey,
            ApiKeyHeaderName = "X-Api-Key",
            ApiKeySecretName = "secret-key-1"
        };

        var table = new Table
        {
            SourceName = domain,
            SchemaName = "dbo",
            TableName = tableName,
            DataSourceType = DataSourceType.HttpDeclarative,
            Sensitivity = "CONFIDENTIAL",
            Description = "Test HTTP table",
            IsActive = true,
            HttpEndpoint = endpoint
        };

        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "dbo", tableName),
            Table = table,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
    }

    [Fact]
    public async Task TestConnection_ValidHttpSource_ReturnsSuccessWithLatency()
    {
        var httpTable = CreateHttpTable("crm", "customers", "https://api.crm-sample.com");
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([httpTable]);

        var fakeHandler = new FakeHttpMessageHandler((req) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var service = new DatasourceTestingService(
            _metadataRepo, _httpClientFactory, _auditRepo, _secretProvider, _environment, NullLogger<DatasourceTestingService>.Instance);

        var result = await service.TestDatasourceAsync("crm.dbo.customers", new DatasourceTestRequest(), CreateAdminUser());

        result.IsSuccess.ShouldBeTrue();
        result.HttpStatusCode.ShouldBe(200);
        result.Diagnostics.TargetHost.ShouldBe("api.crm-sample.com");
        result.Diagnostics.TlsHandshakeSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TestConnection_InvalidCredentials_ReturnsFailureWithStatus401()
    {
        var httpTable = CreateHttpTable("crm", "customers", "https://api.crm-sample.com");
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([httpTable]);

        var fakeHandler = new FakeHttpMessageHandler((req) =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "Unauthorized" };
        });
        var client = new HttpClient(fakeHandler);
        _httpClientFactory.CreateClient("DeclarativeHttp").Returns(client);

        var service = new DatasourceTestingService(
            _metadataRepo, _httpClientFactory, _auditRepo, _secretProvider, _environment, NullLogger<DatasourceTestingService>.Instance);

        var result = await service.TestDatasourceAsync("crm.dbo.customers", new DatasourceTestRequest(), CreateAdminUser());

        result.IsSuccess.ShouldBeFalse();
        result.HttpStatusCode.ShouldBe(401);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("401");
    }

    [Fact]
    public async Task TestConnection_AttemptSsrfToMetadataEndpoint_RejectsEarly()
    {
        // AWS / Cloud metadata IP 169.254.169.254
        var httpTable = CreateHttpTable("cloud", "metadata", "http://169.254.169.254");
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([httpTable]);

        var service = new DatasourceTestingService(
            _metadataRepo, _httpClientFactory, _auditRepo, _secretProvider, _environment, NullLogger<DatasourceTestingService>.Instance);

        var result = await service.TestDatasourceAsync("cloud.dbo.metadata", new DatasourceTestRequest(), CreateAdminUser());

        result.IsSuccess.ShouldBeFalse();
        result.Diagnostics.SecretResolutionStatus.ShouldBe("BlockedBySsrfFilter");
    }

    [Fact]
    public async Task TestConnection_RelativeProbePathTraversal_Rejects()
    {
        var httpTable = CreateHttpTable("crm", "customers", "https://api.crm-sample.com");
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([httpTable]);

        var service = new DatasourceTestingService(
            _metadataRepo, _httpClientFactory, _auditRepo, _secretProvider, _environment, NullLogger<DatasourceTestingService>.Instance);

        var req = new DatasourceTestRequest(RelativeProbePath: "../../etc/passwd");
        var result = await service.TestDatasourceAsync("crm.dbo.customers", req, CreateAdminUser());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("path traversal");
    }

    [Fact]
    public async Task TestConnection_SqlDatasource_ValidSelectOne_ReturnsSuccess()
    {
        var sqlTable = new Table
        {
            SourceName = "sales",
            SchemaName = "dbo",
            TableName = "orders",
            DataSourceType = DataSourceType.Sql,
            Sensitivity = "INTERNAL",
            Description = "SQL Orders Table",
            IsActive = true
        };

        var sqlTableMeta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = sqlTable,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([sqlTableMeta]);

        var service = new DatasourceTestingService(
            _metadataRepo, _httpClientFactory, _auditRepo, _secretProvider, _environment, NullLogger<DatasourceTestingService>.Instance);

        var result = await service.TestDatasourceAsync("sales.dbo.orders", new DatasourceTestRequest(), CreateAdminUser());

        result.IsSuccess.ShouldBeTrue();
        result.HttpStatusCode.ShouldBe(200);
        result.Type.ShouldBe(DataSourceType.Sql);
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
