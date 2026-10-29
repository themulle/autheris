namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
        var client = Substitute.For<IOpenMetadataClient>();
        var repo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "legacy-single-tenant", Enabled = true, WebhookSecret = "om-test-secret-42" }
        };

        var tables = new List<OpenMetadataTable>
        {
            new()
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
        };

        var existing = new TableMetadata
        {
            Identifier = new TableIdentifier("corp", "dbo", "employees"),
            Table = new Table
            {
                SourceName = "corp",
                SchemaName = "dbo",
                TableName = "employees",
                DisplayName = "Employees",
                RequiredFilterColumns = ImmutableArray.Create("tenant_id")
            },
            Columns = [new() { ColumnName = "id", DataType = "int" }, new() { ColumnName = "tenant_id", DataType = "varchar" }]
        };

        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>(tables));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(existing));

        TableMetadata? saved = null;
        await repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => saved = m), Arg.Any<CancellationToken>());

        var sut = new OpenMetadataSyncService(
            client, repo, consentRepo, epochRepo, Options.Create(options), NullLogger<OpenMetadataSyncService>.Instance);

        var result = await sut.SyncPermissionsAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        saved.ShouldNotBeNull();
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

        var repo = Substitute.For<ITableMetadataRepository>();
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

        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(id => id.TableName == "payment"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(existing));

        TableMetadata? saved = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t => saved = t), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TableMetadata>()));

        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(openApi, domain: "payments");

        result.Success.ShouldBeTrue();
        saved.ShouldNotBeNull();
        saved.RequiredFilterColumns.ShouldContain("tenant_id");
    }
}
