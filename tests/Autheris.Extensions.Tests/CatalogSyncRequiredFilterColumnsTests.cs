namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Autheris.Extensions.Tests.TestSupport;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-11: the OpenMetadata and OpenAPI re-ingest paths must keep the persisted RequiredFilterColumns
/// (the DataCatalog variant lives in <see cref="DataCatalogSyncTests"/>).
/// </summary>
public sealed class CatalogSyncRequiredFilterColumnsTests
{
    [Fact]
    public async Task OpenMetadataSync_KeepsRequiredFilterColumns()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "default-tenant", Enabled = true, WebhookSecret = "om-test-secret-42" }
        };

        var existing = new TableMetadata
        {
            Identifier = new TableIdentifier("hr_service", "dbo", "employees"),
            Table = new Table
            {
                SourceName = "hr_service",
                SchemaName = "dbo",
                TableName = "employees",
                DisplayName = "Employees",
                RequiredFilterColumns = ImmutableArray.Create("tenant_id")
            },
            Columns = [new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "tenant_id", DataType = "varchar" }]
        };

        var client = new StubOpenMetadataClient
        {
            Tables =
            [
                new OpenMetadataTable
                {
                    Id = Guid.NewGuid(),
                    Name = "employees",
                    FullyQualifiedName = "hr_service.corp.dbo.employees",
                    Service = new OpenMetadataEntityReference { Name = "hr_service" },
                    DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
                    Description = "Updated description from OpenMetadata",
                    Columns =
                    [
                        new OpenMetadataColumn { Name = "id", DataType = "INT" },
                        new OpenMetadataColumn { Name = "tenant_id", DataType = "VARCHAR" }
                    ]
                }
            ]
        };
        var repo = new InMemoryTableMetadataRepository(existing);
        var consentRepo = new RecordingConsentRepository();
        var epochRepo = new InMemoryPolicyEpochRepository();

        var sut = new OpenMetadataSyncService(
            client, repo, consentRepo, epochRepo, Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance);

        var result = await sut.SyncPermissionsAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        var saved = repo.UpsertedTables.ShouldHaveSingleItem();
        saved.RequiredFilterColumns.ShouldContain("tenant_id");
    }

    [Fact]
    public async Task OpenApiReingest_KeepsRequiredFilterColumns()
    {
        const string openApi = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Payment Service API", "version": "1.0.0" },
          "servers": [ { "url": "https://payments.internal.corp" } ],
          "paths": { "/api/v1/payments": { "get": { "summary": "List all payments" } } },
          "components": {
            "schemas": {
              "Payment": {
                "type": "object",
                "description": "Updated payment description.",
                "properties": {
                  "payment_id": { "type": "string", "format": "uuid" },
                  "tenant_id": { "type": "string" }
                }
              }
            }
          }
        }
        """;

        var tableId = new TableIdentifier("payments", "api", "payment");
        var existing = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SchemaName = "api",
                TableName = "payment",
                DisplayName = "Existing Payment",
                IsActive = true,
                DataSourceType = DataSourceType.HttpDeclarative,
                RequiredFilterColumns = ImmutableArray.Create("tenant_id"),
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    BaseUrl = "https://payments.internal.corp",
                    PathTemplate = "/api/v1/payments",
                    Method = "GET"
                }
            },
            Columns =
            [
                new TableColumn { ColumnName = "payment_id", DataType = "uuid" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var repo = new InMemoryTableMetadataRepository(existing);
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(openApi, domain: "payments");

        result.Success.ShouldBeTrue();
        var saved = repo.UpsertedTables.ShouldHaveSingleItem();
        saved.RequiredFilterColumns.ShouldContain("tenant_id");
    }
}
