namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class LakehouseDataSourceExecutorTests
{
    private sealed class MockColumnMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule)
        {
            if (rawValue == null) return null;
            var str = rawValue.ToString() ?? "";

            return rule.RuleType switch
            {
                "REDACT" => "[REDACTED-GDPR-ART9]",
                "REGEX" when rule.PatternOrFormat == "MASK_EMAIL" => "u***@domain.com",
                "REGEX" when rule.PatternOrFormat == "MASK_LAST_FOUR" => "**** **** **** 1234",
                _ => rawValue
            };
        }
    }

    private sealed class MockLakehouseStorage : ILakehouseStorageProvider
    {
        public ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
        {
            if (location.Contains("metadata.json"))
            {
                return ValueTask.FromResult("""
                {
                  "format-version": 2,
                  "table-uuid": "test-lakehouse-uuid",
                  "location": "s3://lake/orders",
                  "current-snapshot-id": 1001,
                  "current-schema-id": 0,
                  "schemas": [
                    {
                      "schema-id": 0,
                      "fields": [
                        { "id": 1, "name": "orderId", "type": "string" },
                        { "id": 2, "name": "customerEmail", "type": "string" },
                        { "id": 3, "name": "iban", "type": "string" },
                        { "id": 4, "name": "healthData", "type": "string" },
                        { "id": 5, "name": "tenantId", "type": "string" },
                        { "id": 6, "name": "orderDate", "type": "string" }
                      ]
                    }
                  ],
                  "partition-specs": [
                    {
                      "spec-id": 0,
                      "fields": [
                        { "source-id": 5, "field-id": 1000, "name": "tenantId", "transform": "identity" },
                        { "source-id": 6, "field-id": 1001, "name": "orderDate", "transform": "identity" }
                      ]
                    }
                  ],
                  "snapshots": [
                    { "snapshot-id": 1001, "timestamp-ms": 1774692000000, "manifest-list": "s3://lake/orders/metadata/manifest-list.json" }
                  ]
                }
                """);
            }

            if (location.Contains("manifest-list.json"))
            {
                return ValueTask.FromResult("""
                {
                  "entries": [
                    {
                      "file_path": "s3://lake/orders/data/tenantId=tenant-alpha/orderDate=2026-06-01/part-1.parquet",
                      "file_format": "PARQUET",
                      "record_count": 10,
                      "file_size_in_bytes": 4096,
                      "partition": { "tenantId": "tenant-alpha", "orderDate": "2026-06-01" }
                    },
                    {
                      "file_path": "s3://lake/orders/data/tenantId=tenant-beta/orderDate=2026-06-01/part-2.parquet",
                      "file_format": "PARQUET",
                      "record_count": 10,
                      "file_size_in_bytes": 4096,
                      "partition": { "tenantId": "tenant-beta", "orderDate": "2026-06-01" }
                    }
                  ]
                }
                """);
            }

            return ValueTask.FromResult("{}");
        }

        public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(new MemoryStream());

        public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task ExecuteScanAsync_ShouldEnforceTenantPruningAndMasking()
    {
        // Arrange
        var storage = new MockLakehouseStorage();
        var metaReader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);
        var masking = new MockColumnMaskingProvider();

        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Enabled = true,
                Tables = new Dictionary<string, LakehouseTableOptions>
                {
                    ["orders"] = new()
                    {
                        Format = "Iceberg",
                        Location = "s3://lake/orders/metadata/v2.metadata.json",
                        PartitionColumns = ["tenantId", "orderDate"],
                        Sensitivity = "HIGH"
                    }
                }
            }
        });

        var executor = new LakehouseDataSourceExecutor(metaReader, pruner, masking, options, NullLogger<LakehouseDataSourceExecutor>.Instance);

        var request = new LakehouseScanRequest(
            TableName: "orders",
            SelectedColumns: ["orderId", "customerEmail", "iban", "healthData", "orderDate"],
            FilterPredicates: new Dictionary<string, string> { ["orderDate"] = ">= 2026-06-01" },
            TenantId: "tenant-alpha",
            Limit: 5
        );

        // Act
        var result = await executor.ExecuteScanAsync(request);

        // Assert
        result.TableName.ShouldBe("orders");
        // Out of 2 files, the file for tenant-beta must be pruned, leaving exactly 1 file for tenant-alpha
        result.TotalScannedFiles.ShouldBe(1);
        result.TotalPrunedFiles.ShouldBe(1);
        result.PruningEfficiencyPercent.ShouldBe(50.0);

        // Check rows
        result.Rows.Count.ShouldBeGreaterThan(0);
        var firstRow = result.Rows[0];

        // Tenant Isolation
        firstRow["tenantId"].ShouldBe("tenant-alpha");

        // SEC M-35: the ungoverned scan API masks every non-tenant column (fail-closed, no name heuristics)
        firstRow["customerEmail"].ShouldBe("[REDACTED-GDPR-ART9]");
        firstRow["iban"].ShouldBe("[REDACTED-GDPR-ART9]");
        firstRow["healthData"].ShouldBe("[REDACTED-GDPR-ART9]");
        firstRow["orderId"].ShouldBe("[REDACTED-GDPR-ART9]");
    }
}
