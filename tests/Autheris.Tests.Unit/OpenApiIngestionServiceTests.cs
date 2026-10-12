namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class OpenApiIngestionServiceTests
{
    private const string SampleOpenApiJson = """
    {
      "openapi": "3.1.0",
      "info": {
        "title": "Payment Service API",
        "version": "1.0.0",
        "description": "High-throughput enterprise payments and billing microservice."
      },
      "servers": [
        {
          "url": "https://payments.internal.corp"
        }
      ],
      "paths": {
        "/api/v1/payments": {
          "get": {
            "summary": "List all payments"
          }
        },
        "/api/v1/refunds": {
          "get": {
            "summary": "List all refunds"
          }
        }
      },
      "components": {
        "schemas": {
          "Payment": {
            "type": "object",
            "description": "Captured payment transaction record.",
            "x-long-description": "Audit record containing original gateway authorization code and settlement date.",
            "required": ["payment_id", "amount"],
            "properties": {
              "payment_id": {
                "type": "string",
                "format": "uuid",
                "description": "Unique UUID of the payment transaction."
              },
              "amount": {
                "type": "number",
                "format": "decimal",
                "description": "Settled payment amount in gross EUR."
              },
              "pan_masked": {
                "type": "string",
                "description": "Masked credit card primary account number.",
                "x-sensitive": true
              }
            }
          },
          "Refund": {
            "type": "object",
            "description": "Credit note or refund transaction.",
            "properties": {
              "refund_id": {
                "type": "integer",
                "format": "int64",
                "description": "Serial refund identifier."
              },
              "reason": {
                "type": "string",
                "description": "Business justification for the refund."
              }
            }
          }
        }
      }
    }
    """;

    [Fact]
    public async Task IngestOpenApiJsonAsync_ParsesSchemasAndRegistersHttpVirtualTables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var upsertedTables = new List<TableMetadata>();

        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t => upsertedTables.Add(t)), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TableMetadata>()));

        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(SampleOpenApiJson, domain: "payments");

        result.Success.ShouldBeTrue();
        result.ServiceTitle.ShouldBe("Payment Service API");
        result.IngestedTablesCount.ShouldBe(2);
        result.IngestedColumnsCount.ShouldBe(5);
        result.IngestedTableNames.ShouldContain("payment");
        result.IngestedTableNames.ShouldContain("refund");

        upsertedTables.Count.ShouldBe(2);

        var paymentTable = upsertedTables.FirstOrDefault(t => t.Identifier.TableName == "payment");
        paymentTable.ShouldNotBeNull();
        paymentTable.Identifier.Domain.ShouldBe("payments");
        paymentTable.Table.DisplayName.ShouldBe("Payment");
        paymentTable.Table.Description.ShouldBe("Captured payment transaction record.");
        paymentTable.Table.LongDescription!.ShouldContain("Audit record containing original gateway");
        paymentTable.DataSourceType.ShouldBe(DataSourceType.HttpDeclarative);
        paymentTable.HttpEndpoint.ShouldNotBeNull();
        paymentTable.HttpEndpoint!.BaseUrl.ShouldBe("https://payments.internal.corp");
        paymentTable.HttpEndpoint!.PathTemplate.ShouldBe("/api/v1/payments");

        paymentTable.PrimaryKeyColumns.ShouldContain("payment_id");
        paymentTable.Columns.Count.ShouldBe(3);

        var panCol = paymentTable.Columns.First(c => c.ColumnName == "pan_masked");
        panCol.IsSensitive.ShouldBeTrue();
        panCol.Description.ShouldBe("Masked credit card primary account number.");

        var refundTable = upsertedTables.FirstOrDefault(t => t.Identifier.TableName == "refund");
        refundTable.ShouldNotBeNull();
        refundTable.PrimaryKeyColumns.ShouldContain("refund_id");
        refundTable.Columns.Count.ShouldBe(2);

        // SG-11: Newly discovered tables must be created with IsActive = false
        paymentTable.Table.IsActive.ShouldBeFalse();
        refundTable.Table.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task IngestOpenApiJsonAsync_ExistingTable_DoesNotAddPhantomColumns()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var tableId = new TableIdentifier("payments", "api", "payment");

        // Existing table with only payment_id and amount
        var existingMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                SchemaName = "api",
                TableName = "payment",
                DisplayName = "Existing Payment",
                IsActive = true,
                DataSourceType = DataSourceType.HttpDeclarative,
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
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["payment_id"]
        };

        repo.GetTableMetadataAsync(Arg.Is<TableIdentifier>(id => id.TableName == "payment"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(existingMetadata));

        TableMetadata? savedMetadata = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t =>
        {
            if (t.Identifier.TableName == "payment") savedMetadata = t;
        }), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TableMetadata>()));

        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(SampleOpenApiJson, domain: "payments");

        result.Success.ShouldBeTrue();
        savedMetadata.ShouldNotBeNull();
        // pan_masked was in OpenAPI spec but NOT in existing table columns -> must NOT be added!
        savedMetadata.Columns.Any(c => c.ColumnName == "pan_masked").ShouldBeFalse();
        savedMetadata.Columns.Count.ShouldBe(2);
        // Existing table's IsActive state must be preserved
        savedMetadata.Table.IsActive.ShouldBeTrue();
        // Warning should be recorded about ignored phantom column
        result.Warnings.Any(w => w.Contains("new column(s) were ignored")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://127.0.0.1:8080/api")]
    [InlineData("http://localhost:5000/api")]
    [InlineData("ftp://payments.corp.internal")]
    public async Task IngestOpenApiJsonAsync_ForbiddenServerUrl_FailsIngestion(string badUrl)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);

        var specWithBadUrl = SampleOpenApiJson.Replace("https://payments.internal.corp", badUrl);
        var result = await service.IngestOpenApiJsonAsync(specWithBadUrl, domain: "payments");

        result.Success.ShouldBeFalse();
        result.Warnings.Any(w => w.Contains("violates outbound egress") || w.Contains("Invalid server URL")).ShouldBeTrue();
        await repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestOpenApiJsonAsync_Swagger20_ExtractsDefinitionsAndBuildsServerUrl()
    {
        const string swagger2Json = """
        {
          "swagger": "2.0",
          "info": { "title": "Legacy Crane API", "version": "1.0.0" },
          "host": "cranes.corp.internal",
          "basePath": "/v2",
          "schemes": ["https"],
          "paths": {
            "/cranes": {
              "get": { "summary": "List cranes" },
              "post": { "summary": "Create crane" }
            }
          },
          "definitions": {
            "CraneInfo": {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "model": { "type": "string" }
              }
            }
          }
        }
        """;

        var repo = Substitute.For<ITableMetadataRepository>();
        TableMetadata? saved = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t => saved = t), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TableMetadata>()));

        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(swagger2Json, domain: "logistics");

        result.Success.ShouldBeTrue();
        result.IngestedTablesCount.ShouldBe(1);
        result.IngestedTableNames.ShouldContain("craneinfo");
        result.SkippedTableNames.ShouldNotBeNull();
        result.SkippedTableNames.ShouldContain("POST /cranes");
        saved.ShouldNotBeNull();
        saved.Table.HttpEndpoint?.BaseUrl.ShouldBe("https://cranes.corp.internal/v2");
        saved.Table.IsActive.ShouldBeFalse(); // SEC M-30 inaktiv-start
    }

    [Fact]
    public async Task IngestOpenApiJsonAsync_WithAuthCredentials_StoresInSecretProviderWithoutLeaking()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        TableMetadata? saved = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(t => saved = t), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TableMetadata>()));

        const string rawApiKey = "my-secret-api-key-8888";
        var auth = new DatasourceAuthDto("apiKey", Name: "X-CUSTOM-KEY", Value: rawApiKey);

        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance, secretProvider: secretProvider);
        var result = await service.IngestOpenApiJsonAsync(SampleOpenApiJson, "payments", defaultBaseUrl: null, auth: auth, dryRun: false);

        result.Success.ShouldBeTrue();
        saved.ShouldNotBeNull();
        saved.Table.HttpEndpoint?.AuthMode.ShouldBe(HttpAuthMode.StaticApiKey);
        saved.Table.HttpEndpoint?.ApiKeyHeaderName.ShouldBe("X-CUSTOM-KEY");
        saved.Table.HttpEndpoint?.ApiKeySecretName.ShouldNotBeNull();
        saved.Table.HttpEndpoint?.ApiKeySecretName.ShouldNotBe(rawApiKey);

        // SecretProvider received the raw secret bytes
        secretProvider.Received().SetSecret(
            saved.Table.HttpEndpoint!.ApiKeySecretName!,
            Arg.Is<byte[]>(b => System.Text.Encoding.UTF8.GetString(b) == rawApiKey));
    }
}
