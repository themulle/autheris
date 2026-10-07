using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Autheris.Tests.Unit.Services;

public sealed class SyntheticDataAndExceptionHandlingTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly IConsentResolutionService _resolutionService = Substitute.For<IConsentResolutionService>();
    private readonly IConsentCacheService _cacheService = Substitute.For<IConsentCacheService>();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();
    private readonly IChunkedQueryExecutor _chunkedExecutor = new ChunkedQueryExecutor(500);

    private readonly TableIdentifier _testTableId = new("crm", "dbo", "customers");

    public SyntheticDataAndExceptionHandlingTests()
    {
        var meta = new TableMetadata
        {
            Identifier = _testTableId,
            Table = new Table
            {
                IsActive = true,
                DataSourceType = DataSourceType.Sql,
                SourceType = "SqlServer",
                SourceName = "crm_db",
                SchemaName = "dbo",
                TableName = "customers"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "nvarchar(50)" }
            ]
        };

        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(meta);

        var decision = TableAccessDecision.Allowed(_testTableId, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear
        }, null, hasUnconstrainedColumnAllow: true);

        _resolutionService.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(decision);
    }

    [Fact]
    public async Task GatewayExecutionService_WithoutRegisteredSqlExecutor_ThrowsGatewayNotImplementedException()
    {
        // S-1: When no IDataSourceExecutor is registered for DataSourceType.Sql,
        // GatewayExecutionService must NOT fall back to a synthetic dummy executor.
        // It must throw GatewayNotImplementedException (501).
        var service = new GatewayExecutionService(
            _metadataRepo,
            _consentRepo,
            _auditRepo,
            _resolutionService,
            _cacheService,
            _maskingProvider,
            _chunkedExecutor,
            Options.Create(new GatewayOptions()),
            drainController: null,
            dataSourceExecutors: []); // explicitly empty

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-123"),
            new Claim("tenant_id", "tenant-1")
        ], "TestAuth"));

        await Assert.ThrowsAsync<GatewayNotImplementedException>(() =>
            service.ExecuteTableQueryAsync(principal, _testTableId));
    }

    [Fact]
    public async Task SqlDataSourceExecutor_WithNullEnvironment_TreatsAsProductionAndThrowsGatewayNotImplementedException()
    {
        // S-1: When environment is null, fail-closed production must be assumed.
        // Without active connection or AreExternalSystemsMockedIfUnreachable, it must throw GatewayNotImplementedException.
        var executor = new SqlDataSourceExecutor(
            connectionFactory: null,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<SqlDataSourceExecutor>.Instance,
            environment: null); // null environment

        var meta = new TableMetadata
        {
            Identifier = _testTableId,
            Table = new Table { IsActive = true, DataSourceType = DataSourceType.Sql, SourceType = "SqlServer", SourceName = "crm_db" }
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-123")], "Test"));
        var decision = TableAccessDecision.Allowed(_testTableId, new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear
        }, null, hasUnconstrainedColumnAllow: true);

        var context = new DataSourceExecutionContext(
            SourceName: "crm_db",
            Metadata: meta,
            Principal: principal,
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id"]
        );

        await Assert.ThrowsAsync<GatewayNotImplementedException>(() =>
            executor.ExecuteAsync(context));
    }

    [Fact]
    public async Task GovernedSqlExecutionService_WithNullEnvironment_TreatsAsProductionAndThrowsGatewayNotImplementedException()
    {
        // S-1: When environment is null, fail-closed production must be assumed.
        // WebSQL without connection options must throw GatewayNotImplementedException, NOT generate synthetic rows.
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "crm_db", AllowedDataSources = ["crm_db"] }
        });
        var service = new GovernedSqlExecutionService(
            options,
            consentResolution: _resolutionService,
            consentRepository: _consentRepo,
            tableRepository: _metadataRepo,
            environment: null); // null environment

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-123"),
            new Claim("tenant_id", "tenant-1")
        ], "TestAuth"));

        var request = new GovernedSqlQueryRequest("SELECT id, name FROM dbo.customers", DataSourceName: "crm_db");

        await Assert.ThrowsAsync<GatewayNotImplementedException>(() =>
            service.ExecuteGovernedQueryAsync(request, principal, new TenantId("tenant-1"), (reader, ct) => Task.CompletedTask));
    }

    [Fact]
    public async Task GatewayExceptionHandler_DoesNotLeakRawNotSupportedExceptionMessage()
    {
        // S-2: Raw NotSupportedException must not leak internal driver/class details in 501 problem details.
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var secretDriverError = "DriverInternalLeak.NativeConnection: segfault at 0xdeadbeef";
        var ex = new NotSupportedException(secretDriverError);

        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var title = doc.RootElement.GetProperty("title").GetString();

        Assert.DoesNotContain("DriverInternalLeak", title);
        Assert.DoesNotContain("0xdeadbeef", title);
    }
}
