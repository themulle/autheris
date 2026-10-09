namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlCrossSourceGuardTests
{
    private const string Tenant = "tenant_test";

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string table, DataSourceType dsType)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = "PostgreSql",
                DataSourceType = dsType,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };
    }

    [Theory]
    [InlineData(DataSourceType.HttpDeclarative)]
    [InlineData(DataSourceType.HttpPlugin)]
    [InlineData(DataSourceType.LakehouseIceberg)]
    [InlineData(DataSourceType.LakehouseDelta)]
    public async Task HttpDeclarativeTable_InWebSqlPushdown_IsRejected(DataSourceType dataSourceType)
    {
        var tableMeta = CreateTable("api_data", dataSourceType);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(tableMeta));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci =>
            {
                var tableId = ci.ArgAt<TableIdentifier>(3);
                var columnAccess = new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear
                };
                return TableAccessDecision.Allowed(tableId, columnAccess, null, hasUnconstrainedColumnAllow: false);
            });

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "default",
                AllowedDataSources = ["sales"]
            }
        };

        var factory = Substitute.For<ISqlConnectionFactory>();

        var service = new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: repo,
            connectionFactory: factory,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            secretProvider: null,
            sessionInitializer: null);

        var request = new GovernedSqlQueryRequest("SELECT id, name FROM sales.public.api_data");

        // Act & Assert: Must fail-closed with WebSqlPolicyException
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, CreateUser(), new TenantId(Tenant)));

        ex.Message.ShouldContain("data source type");

        // Verify ISqlConnectionFactory was never invoked!
        await factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public async Task DuckDbOlap_HttpTable_DoesNotFallBackToDefaultSqlConnector()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new Claim("tenant_id", "t1")
        ], "Bearer"));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(auditRepo);
        httpContext.RequestServices = services.BuildServiceProvider();

        var body = JsonSerializer.Serialize(new
        {
            sql = "SELECT id, name FROM sales.public.http_table",
            tableNames = new[] { "sales.public.http_table" }
        });
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var table = new TableIdentifier("sales", "public", "http_table");
        var meta = CreateTable("http_table", DataSourceType.HttpDeclarative);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>())
            .Returns(meta);

        // Registry only has "default-sql"
        var sqlConnector = Substitute.For<IAutherisConnector>();
        sqlConnector.ConnectorId.Returns("default-sql");
        var recordSource = Substitute.For<IConnectorRecordSource>();
        sqlConnector.RecordSource.Returns(recordSource);

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(table, out Arg.Any<IAutherisConnector>()!)
            .Returns(x => { x[1] = sqlConnector; return true; });

        var accessResolver = Substitute.For<ICrossDomainAccessResolver>();
        accessResolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var options = Options.Create(new GatewayOptions
        {
            Profile = "Strict",
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true }
        });
        var engine = Substitute.For<IDuckDbOlapEngine>();

        // Act
        await DuckDbOlapEndpoints.HandleOlapQueryAsync(
            httpContext,
            engine,
            metadataRepo,
            registry,
            accessResolver,
            Substitute.For<IColumnMaskingProvider>(),
            options,
            NullLoggerFactory.Instance);

        // Assert: Must be rejected with 403 Forbidden and the SQL record source must never be called!
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await recordSource.DidNotReceiveWithAnyArgs().ReadBatchAsync(default!, default!, default);
    }
}
