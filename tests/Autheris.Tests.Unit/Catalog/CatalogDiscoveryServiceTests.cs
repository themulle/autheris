namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogDiscoveryServiceTests
{
    private readonly ITableMetadataRepository _metadataRepository = Substitute.For<ITableMetadataRepository>();
    private readonly IRebacEvaluator _rebacEvaluator = Substitute.For<IRebacEvaluator>();
    private readonly IAuditLogRepository _auditLogRepository = Substitute.For<IAuditLogRepository>();
    private readonly IKeyVaultSecretProvider _secretProvider = Substitute.For<IKeyVaultSecretProvider>();
    private readonly IOpenApiIngestionService _openApiIngestionService = Substitute.For<IOpenApiIngestionService>();

    private CatalogDiscoveryService CreateService()
    {
        return new CatalogDiscoveryService(
            _metadataRepository,
            _auditLogRepository,
            _rebacEvaluator,
            _secretProvider,
            _openApiIngestionService,
            NullLogger<CatalogDiscoveryService>.Instance);
    }

    [Fact]
    public async Task ListDatasetsAsync_FiltersUnpermittedDatasets_CallerOnlySeesAllowedDatasets()
    {
        // Arrange
        var service = CreateService();
        var tenant = new TenantId("tenant-a");
        var userSid = new Sid("user:alice");
        var context = new RequestContext(tenant, userSid);

        var tableAllowedId = new TableIdentifier("crm", "dbo", "customers");
        var tableDeniedId = new TableIdentifier("finance", "dbo", "salaries");

        var tableAllowed = new TableMetadata
        {
            Identifier = tableAllowedId,
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "customers",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true,
                Description = "Customer records"
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var tableDenied = new TableMetadata
        {
            Identifier = tableDeniedId,
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "salaries",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true,
                Description = "Confidential salaries"
            },
            Columns = [new TableColumn { ColumnName = "amount", DataType = "decimal" }]
        };

        _metadataRepository.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([tableAllowed, tableDenied]);

        _rebacEvaluator.IsEnabled.Returns(true);

        _rebacEvaluator.CheckAsync(
                Arg.Is<RebacCheckRequest>(r => r.TenantId == tenant.Value && r.User == userSid.Value && r.Object == RebacTableGate.ObjectId(tableAllowedId) && r.Relation == "can_query"),
                Arg.Any<CancellationToken>())
            .Returns(RebacCheckResult.Permitted);

        _rebacEvaluator.CheckAsync(
                Arg.Is<RebacCheckRequest>(r => r.TenantId == tenant.Value && r.User == userSid.Value && r.Object == RebacTableGate.ObjectId(tableDeniedId) && r.Relation == "can_query"),
                Arg.Any<CancellationToken>())
            .Returns(RebacCheckResult.Denied);

        // Act
        var result = await service.ListDatasetsAsync(context);

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result[0].DatasetId.ShouldBe(tableAllowedId.ToString());
        result[0].Domain.ShouldBe("crm");
        result[0].Table.ShouldBe("customers");
        result.Any(d => d.DatasetId == tableDeniedId.ToString()).ShouldBeFalse();
    }

    [Fact]
    public async Task GetDatasetDetailAsync_WhenAllowed_ReturnsDetail_WhenDenied_ReturnsNull()
    {
        // Arrange
        var service = CreateService();
        var tenant = new TenantId("tenant-a");
        var userSid = new Sid("user:alice");
        var context = new RequestContext(tenant, userSid);

        var tableAllowedId = new TableIdentifier("crm", "dbo", "customers");
        var tableDeniedId = new TableIdentifier("finance", "dbo", "salaries");

        var tableAllowed = new TableMetadata
        {
            Identifier = tableAllowedId,
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "customers",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true,
                Description = "Customer records"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "string", IsSensitive = true }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var tableDenied = new TableMetadata
        {
            Identifier = tableDeniedId,
            Table = new Table
            {
                SchemaName = "dbo",
                TableName = "salaries",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "amount", DataType = "decimal" }]
        };

        _metadataRepository.GetTableMetadataAsync(tableAllowedId, Arg.Any<CancellationToken>())
            .Returns(tableAllowed);
        _metadataRepository.GetTableMetadataAsync(tableDeniedId, Arg.Any<CancellationToken>())
            .Returns(tableDenied);

        _rebacEvaluator.IsEnabled.Returns(true);
        _rebacEvaluator.CheckAsync(
                Arg.Is<RebacCheckRequest>(r => r.Object == RebacTableGate.ObjectId(tableAllowedId) && r.Relation == "can_query"),
                Arg.Any<CancellationToken>())
            .Returns(RebacCheckResult.Permitted);
        _rebacEvaluator.CheckAsync(
                Arg.Is<RebacCheckRequest>(r => r.Object == RebacTableGate.ObjectId(tableDeniedId) && r.Relation == "can_query"),
                Arg.Any<CancellationToken>())
            .Returns(RebacCheckResult.Denied);

        // Act
        var allowedDetail = await service.GetDatasetDetailAsync(tableAllowedId, context);
        var deniedDetail = await service.GetDatasetDetailAsync(tableDeniedId, context);

        // Assert
        allowedDetail.ShouldNotBeNull();
        allowedDetail.DatasetId.ShouldBe(tableAllowedId.ToString());
        allowedDetail.Columns.Count.ShouldBe(2);
        allowedDetail.Columns.First(c => c.Name == "email").IsPiiIndicator.ShouldBeTrue();
        allowedDetail.Columns.First(c => c.Name == "id").IsPrimaryKey.ShouldBeTrue();

        deniedDetail.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterDatasource_NeverLeaksSecretInResponseOrAudit()
    {
        // Arrange
        var service = CreateService();
        var tenant = new TenantId("tenant-a");
        var adminSid = new Sid("user:admin");
        var context = new RequestContext(tenant, adminSid, Roles: ["GovernanceAdmin"]);

        const string rawSecret = "super-secret-vault-api-key-99999";
        var request = new DatasourceRegistrationRequest(
            Name: "CraneTelemetry",
            Domain: "telemetry",
            SpecContent: """
            {
              "openapi": "3.0.0",
              "info": { "title": "Crane Telemetry API", "version": "1.0.0" },
              "paths": {
                "/cranes": {
                  "get": { "summary": "Get cranes" }
                }
              },
              "components": {
                "schemas": {
                  "Crane": {
                    "type": "object",
                    "properties": {
                      "crane_id": { "type": "string" },
                      "crane_status": { "type": "string" },
                      "lat": { "type": "number" },
                      "lon": { "type": "number" }
                    }
                  }
                }
              }
            }
            """,
            Auth: new DatasourceAuthDto(
                Type: "apiKey",
                Name: "X-API-KEY",
                Value: rawSecret
            )
        );

        _openApiIngestionService.IngestOpenApiJsonAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<DatasourceAuthDto?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new OpenApiIngestionResult(
                Success: true,
                ServiceTitle: "Crane Telemetry API",
                IngestedTablesCount: 1,
                IngestedColumnsCount: 4,
                IngestedTableNames: ["crane"],
                Warnings: []
            ));

        AuditLogEntry? capturedAudit = null;
        await _auditLogRepository.RecordAuditEventAsync(
            Arg.Do<AuditLogEntry>(e => capturedAudit = e),
            Arg.Any<CancellationToken>());

        // Act
        var result = await service.RegisterDatasourceAsync(request, context);

        // Assert
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.IsConfigured.ShouldBeTrue();
        result.CreatedDatasets.ShouldContain("crane");

        // 1. Never leak raw secret in Response or serialized response
        var responseJson = JsonSerializer.Serialize(result);
        responseJson.ShouldNotContain(rawSecret);
        result.ErrorMessage.ShouldBeNull();

        // 2. Secret MUST be persisted in IKeyVaultSecretProvider
        _secretProvider.Received().SetSecret(
            Arg.Any<string>(),
            Arg.Is<byte[]>(b => System.Text.Encoding.UTF8.GetString(b) == rawSecret));

        // 3. Never leak raw secret in AuditLog
        capturedAudit.ShouldNotBeNull();
        capturedAudit!.DetailsJson.ShouldNotContain(rawSecret);
        capturedAudit.DetailsJson.ShouldContain("CraneTelemetry");
    }
}
