namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class LakehouseIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _testLakehouseDir;

    public LakehouseIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _testLakehouseDir = Path.Combine(Path.GetTempPath(), "iceberg_integration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testLakehouseDir);

        // Setup test Iceberg metadata and manifest files
        var metaDir = Path.Combine(_testLakehouseDir, "orders", "metadata");
        Directory.CreateDirectory(metaDir);

        var metaJsonPath = Path.Combine(metaDir, "v2.metadata.json");
        var manifestJsonPath = Path.Combine(metaDir, "manifest-list.json");

        File.WriteAllText(metaJsonPath, $$"""
        {
          "format-version": 2,
          "table-uuid": "lakehouse-orders-uuid",
          "location": "{{metaJsonPath}}",
          "last-sequence-number": 1,
          "last-updated-ms": 1774692000000,
          "current-snapshot-id": 5001,
          "current-schema-id": 0,
          "schemas": [
            {
              "schema-id": 0,
              "fields": [
                { "id": 1, "name": "orderId", "type": "string" },
                { "id": 2, "name": "customerEmail", "type": "string" },
                { "id": 3, "name": "iban", "type": "string" },
                { "id": 4, "name": "healthData", "type": "string" },
                { "id": 5, "name": "secretInternalNotes", "type": "string" },
                { "id": 6, "name": "tenantId", "type": "string" },
                { "id": 7, "name": "orderDate", "type": "string" }
              ]
            }
          ],
          "default-spec-id": 0,
          "partition-specs": [
            {
              "spec-id": 0,
              "fields": [
                { "source-id": 6, "field-id": 1000, "name": "tenantId", "transform": "identity" },
                { "source-id": 7, "field-id": 1001, "name": "orderDate", "transform": "identity" }
              ]
            }
          ],
          "snapshots": [
            {
              "snapshot-id": 5001,
              "timestamp-ms": 1774692000000,
              "manifest-list": "{{manifestJsonPath}}"
            }
          ]
        }
        """);

        File.WriteAllText(manifestJsonPath, """
        {
          "entries": [
            {
              "file_path": "orders/data/part-1.parquet",
              "file_format": "PARQUET",
              "record_count": 5,
              "file_size_in_bytes": 1024,
              "partition": { "tenantId": "tenant-lake-a", "orderDate": "2026-06-01" },
              "lower_bounds": { "orderDate": "2026-06-01" },
              "upper_bounds": { "orderDate": "2026-06-01" }
            },
            {
              "file_path": "orders/data/part-2.parquet",
              "file_format": "PARQUET",
              "record_count": 5,
              "file_size_in_bytes": 1024,
              "partition": { "tenantId": "tenant-lake-b", "orderDate": "2026-06-02" },
              "lower_bounds": { "orderDate": "2026-06-02" },
              "upper_bounds": { "orderDate": "2026-06-02" }
            }
          ]
        }
        """);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:Lakehouse:Enabled", "true");
            builder.UseSetting("Gateway:Lakehouse:Storage:Provider", "Local");
            builder.UseSetting("Gateway:Lakehouse:Storage:LocalBasePath", _testLakehouseDir);
            builder.UseSetting("Gateway:Lakehouse:Tables:iceberg_orders:Location", metaJsonPath);
            builder.UseSetting("Gateway:Lakehouse:Tables:iceberg_orders:Format", "Iceberg");
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testLakehouseDir))
            {
                Directory.Delete(_testLakehouseDir, true);
            }
        }
        catch
        {
            // Ignore test cleanup error
        }
    }

    [Fact]
    public void LakehouseServices_ShouldBeRegisteredInDependencyInjection()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var storageProvider = sp.GetService<ILakehouseStorageProvider>();
        storageProvider.ShouldNotBeNull();

        var metaReader = sp.GetService<IIcebergMetadataReader>();
        metaReader.ShouldNotBeNull();

        var pruner = sp.GetService<IIcebergPartitionPruner>();
        pruner.ShouldNotBeNull();

        var lakehouseExecutor = sp.GetService<ILakehouseDataSourceExecutor>();
        lakehouseExecutor.ShouldNotBeNull();

        var dataExecutors = sp.GetServices<IDataSourceExecutor>().ToList();
        dataExecutors.Any(e => e.SupportedType == DataSourceType.LakehouseIceberg).ShouldBeTrue();
    }

    [Fact]
    public async Task LakehouseExecutor_ThroughExecutionService_ShouldEnforcePruningMaskingAndTenantIsolation()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var dataExecutors = sp.GetServices<IDataSourceExecutor>();
        var lakehouseExecutor = dataExecutors.FirstOrDefault(e => e.SupportedType == DataSourceType.LakehouseIceberg);
        lakehouseExecutor.ShouldNotBeNull();

        var tableId = new TableIdentifier("lakehouse", "public", "iceberg_orders");
        var table = new Table
        {
            SourceName = "lakehouse",
            SchemaName = "public",
            TableName = "iceberg_orders",
            DataSourceType = DataSourceType.LakehouseIceberg
        };

        var metadata = new TableMetadata
        {
            Table = table,
            Identifier = tableId,
            Columns =
            [
                new TableColumn { ColumnName = "orderId", DataType = "string" },
                new TableColumn { ColumnName = "customerEmail", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "iban", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "healthData", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "secretInternalNotes", DataType = "string", IsSensitive = true },
                new TableColumn { ColumnName = "tenantId", DataType = "string" },
                new TableColumn { ColumnName = "orderDate", DataType = "string" }
            ],
            // SEC M-35: masking is rule-based (catalog masking rules), no longer derived from column names
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["customerEmail"] = new MaskingRule { RuleType = "MASK_EMAIL" },
                ["iban"] = new MaskingRule { RuleType = "MASK_IBAN" }
            }
        };

        var decision = new TableAccessDecision(
            Table: tableId,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>
            {
                ["orderId"] = ColumnAccessLevel.Clear,
                ["customerEmail"] = ColumnAccessLevel.Mask,
                ["iban"] = ColumnAccessLevel.Mask,
                ["healthData"] = ColumnAccessLevel.Mask,
                ["secretInternalNotes"] = ColumnAccessLevel.Deny, // Denied column
                ["tenantId"] = ColumnAccessLevel.Clear,
                ["orderDate"] = ColumnAccessLevel.Clear
            },
            CombinedRowFilterSql: null,
            DeniedReasons: []
        );

        var execContext = new DataSourceExecutionContext(
            SourceName: "lakehouse",
            Metadata: metadata,
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "analyst-1")])),
            AccessDecision: decision,
            Arguments: new Dictionary<string, object?>
            {
                ["where"] = new Dictionary<string, object?>
                {
                    ["orderDate"] = new Dictionary<string, object?> { ["eq"] = "2026-06-01" }
                }
            },
            RequestedFields: ["orderId", "customerEmail", "iban", "healthData", "secretInternalNotes", "tenantId", "orderDate"],
            Tenant: new TenantId("tenant-lake-a"),
            Limit: 10
        );

        // Act
        var rows = await lakehouseExecutor.ExecuteAsync(execContext);

        // Assert
        rows.Count.ShouldBeGreaterThan(0);
        foreach (var row in rows)
        {
            // Tenant isolation
            row["tenantId"].ShouldBe("tenant-lake-a");

            // Order date matches filter
            row["orderDate"].ShouldBe("2026-06-01");

            // Denied column stripped
            row.ContainsKey("secretInternalNotes").ShouldBeFalse();

            // Column masking applied
            row["customerEmail"]?.ToString().ShouldNotBeNull().ShouldContain("***");
            row["iban"]?.ToString().ShouldNotBeNull().ShouldContain("****");
            row["healthData"]?.ToString().ShouldNotBeNull().ShouldContain("REDACTED");
        }
    }
}
