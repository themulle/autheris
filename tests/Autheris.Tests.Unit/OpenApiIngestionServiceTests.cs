namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
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
    }
}
