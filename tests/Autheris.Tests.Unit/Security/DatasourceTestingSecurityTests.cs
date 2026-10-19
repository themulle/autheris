namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security condition SEC-SEC210-03 (REQ-SEC-210):
/// Pre-flight connectivity testing resolves from GatewayOptions when catalog is unpopulated,
/// validates identifier format, and returns structured 404 rather than unhandled 500.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatasourceTestingSecurityTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ISqlConnectionFactory _connectionFactory = Substitute.For<ISqlConnectionFactory>();

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "t1")], "Test"));

    private DatasourceTestingService CreateService(GatewayOptions? options = null)
    {
        // Simulate empty catalog before table ingestion / initial onboarding
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([]));

        var gatewayOptions = options ?? new GatewayOptions();

        return new DatasourceTestingService(
            _metadataRepo,
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            _auditRepo,
            secretProvider: null,
            environment: null,
            logger: null,
            sqlConnectionFactory: _connectionFactory,
            gateway: Options.Create(gatewayOptions),
            sqlSourceDescriber: new SqlSourceDescriber());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_SEC210_03_PreFlightTest_UnpopulatedCatalog_ResolvesFromGatewayOptions()
    {
        // SEC-SEC210-03: When catalog is empty, resolve connection from GatewayOptions.DataSources.Connections.
        const string datasourceId = "SqlServer_Onboarding";
        var gateway = new GatewayOptions();
        gateway.DataSources.Connections[datasourceId] = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=10.160.2.94,1433;Database=TestDb;Integrated Security=true;"
        };

        _connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new SqliteConnection()));

        var service = CreateService(gateway);
        var result = await service.TestDatasourceAsync(datasourceId, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeTrue();
        result.DatasourceId.ShouldBe(datasourceId);
        result.Diagnostics.TargetHost.ShouldBe("10.160.2.94");
        result.Diagnostics.TargetPort.ShouldBe(1433);
        await _connectionFactory.Received(1).CreateOpenConnectionAsync(Arg.Is<DataSourceConnectionOptions>(c => c.Provider == "SqlServer"), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_SEC210_03_PreFlightTest_UnconfiguredDatasource_ThrowsKeyNotFoundException_ForStructured404()
    {
        // SEC-SEC210-03: When datasource is not configured anywhere, throws KeyNotFoundException (mapped to 404).
        const string unknownId = "NonExistent_Datasource";
        var service = CreateService();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            service.TestDatasourceAsync(unknownId, new DatasourceTestRequest(), Admin()));

        ex.Message.ShouldContain(unknownId);
        ex.Message.ShouldContain("not found in catalog or configuration");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\win.ini")]
    [InlineData("data/source/path")]
    [InlineData("data\\source")]
    [InlineData("data\r\ninjection")]
    [InlineData("data\0nullbyte")]
    [InlineData("data source with spaces")]
    [InlineData("data;DROP TABLE TABLES;--")]
    [Trait("Category", "Security")]
    public async Task T_SEC_SEC210_03_TestDatasource_RejectsMalformedOrMaliciousIdentifiers(string invalidId)
    {
        // SEC-SEC210-03: Strict identifier validation rejects directory traversal, control characters, and injection attempts.
        var service = CreateService();

        DatasourceTestingService.IsValidDatasourceId(invalidId).ShouldBeFalse();

        await Should.ThrowAsync<ArgumentException>(() =>
            service.TestDatasourceAsync(invalidId, new DatasourceTestRequest(), Admin()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_SEC210_03_PreFlightTest_OpenFailure_ReturnsStructuredResultWithoutExposingSecrets()
    {
        // SEC-SEC210-03: SQL connection failure returns structured DatasourceTestResult with redacted diagnostics.
        const string datasourceId = "ProdSql";
        var gateway = new GatewayOptions();
        gateway.DataSources.Connections[datasourceId] = new DataSourceConnectionOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=sql.corp.internal,1433;Database=Finances;User ID=admin;Password=vault_secret_123;"
        };

        _connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw new TimeoutException("Database connection timed out with secret password=vault_secret_123;"));

        var service = CreateService(gateway);
        var result = await service.TestDatasourceAsync(datasourceId, new DatasourceTestRequest(), Admin());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("vault_secret_123");
        result.Diagnostics.ProbeDetails["Category"].ShouldBe("ConnectionFailed");
    }
}
