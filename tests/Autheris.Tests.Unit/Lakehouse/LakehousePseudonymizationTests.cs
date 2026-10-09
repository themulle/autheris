namespace Autheris.Tests.Unit.Lakehouse;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// POL-4: Lakehouse- und DeltaLake-Executor: verschiedene Pseudonyme je Mandant, keine Doppelmaskierung.
/// </summary>
public sealed class LakehousePseudonymizationTests
{
    private readonly IOptions<GatewayOptions> _gatewayOptions = Options.Create(new GatewayOptions
    {
        DataMasking = new DataMaskingOptions
        {
            HmacSecretKeyVaultRef = "dev-salt-key-for-tenant-pseudonyms-32bytes-long",
            HmacKeyId = "master-key"
        },
        Lakehouse = new LakehouseOptions
        {
            Enabled = true,
            Tables = new Dictionary<string, LakehouseTableOptions>
            {
                ["orders"] = new()
                {
                    Format = "Iceberg",
                    Location = "s3://lake/orders",
                    Sensitivity = "HIGH"
                }
            }
        }
    });

    private readonly ColumnMaskingProvider _maskingProvider;

    public LakehousePseudonymizationTests()
    {
        _maskingProvider = new ColumnMaskingProvider(_gatewayOptions);
    }

    [Fact]
    public void MaskingRule_CreateTenantScopedHmacRule_GeneratesDistinctPseudonymsPerTenant()
    {
        const string cleartext = "customer@example.com";
        var baseRule = new MaskingRule { RuleType = "HMAC", HmacKeyId = "lake-key" };

        var ruleTenantA = MaskingRule.CreateTenantScopedHmacRule(baseRule, "tenant-a");
        var ruleTenantB = MaskingRule.CreateTenantScopedHmacRule(baseRule, "tenant-b");

        var pseudonymA = _maskingProvider.MaskValue("email", cleartext, ruleTenantA)?.ToString();
        var pseudonymB = _maskingProvider.MaskValue("email", cleartext, ruleTenantB)?.ToString();

        pseudonymA.ShouldNotBeNull();
        pseudonymB.ShouldNotBeNull();
        pseudonymA.ShouldNotBe(cleartext);
        pseudonymB.ShouldNotBe(cleartext);
        pseudonymA.ShouldNotBe(pseudonymB);

        // Deterministic within same tenant
        var pseudonymA2 = _maskingProvider.MaskValue("email", cleartext, ruleTenantA)?.ToString();
        pseudonymA2.ShouldBe(pseudonymA);
    }

    [Fact]
    public async Task DeltaLakeExecutor_ProducesDistinctPseudonymsPerTenant_AndSetsInDbMaskingExecuted()
    {
        var reader = Substitute.For<IDeltaMetadataReader>();
        var pruner = new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance);

        var tableId = new TableIdentifier("lake", "sales", "delta_orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "delta_orders", DataSourceType = DataSourceType.LakehouseDelta },
            Columns =
            [
                new TableColumn { ColumnName = "tenantId", DataType = "string" },
                new TableColumn { ColumnName = "secret_col", DataType = "string", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["secret_col"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        var files = new List<DeltaDataFile>
        {
            new("file1.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-a" }, 1, 0, true, 1,
                new Dictionary<string, string> { ["tenantId"] = "tenant-a" },
                new Dictionary<string, string> { ["tenantId"] = "tenant-a" }),
            new("file2.parquet", new Dictionary<string, string> { ["tenantId"] = "tenant-b" }, 1, 0, true, 1,
                new Dictionary<string, string> { ["tenantId"] = "tenant-b" },
                new Dictionary<string, string> { ["tenantId"] = "tenant-b" })
        };

        var snapshot = new DeltaSnapshot(
            "s3://lake/delta_orders", 1, 0,
            new DeltaTableMetadata("id", "delta_orders", null, "parquet",
                new DeltaSchema("struct", [new DeltaField("tenantId", "string"), new DeltaField("secret_col", "string")]),
                ["tenantId"], 0, new Dictionary<string, string>()),
            files,
            new DeltaProtocol(1, 2));

        reader.LoadSnapshotAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeltaSnapshot>(snapshot));

        var executor = new DeltaLakeDataSourceExecutor(
            reader,
            pruner,
            _maskingProvider,
            _gatewayOptions,
            NullLogger<DeltaLakeDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        // 1. Tenant A
        var contextA = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["tenantId"] = ColumnAccessLevel.Clear,
                ["secret_col"] = ColumnAccessLevel.Mask
            }),
            new Dictionary<string, object?>(),
            new List<string> { "tenantId", "secret_col" },
            null,
            1000,
            0,
            new TenantId("tenant-a"));

        var rowsA = await executor.ExecuteAsync(contextA);
        contextA.Items.TryGetValue("InDbColumnMaskingExecuted", out var maskedA).ShouldBeTrue();
        maskedA.ShouldBe(true);
        rowsA.Count.ShouldBe(1);
        var pseudoA = rowsA[0]["secret_col"]?.ToString();
        pseudoA.ShouldNotBeNull();

        // 2. Tenant B
        var contextB = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["tenantId"] = ColumnAccessLevel.Clear,
                ["secret_col"] = ColumnAccessLevel.Mask
            }),
            new Dictionary<string, object?>(),
            new List<string> { "tenantId", "secret_col" },
            null,
            1000,
            0,
            new TenantId("tenant-b"));

        var rowsB = await executor.ExecuteAsync(contextB);
        contextB.Items.TryGetValue("InDbColumnMaskingExecuted", out var maskedB).ShouldBeTrue();
        maskedB.ShouldBe(true);
        rowsB.Count.ShouldBe(1);
        var pseudoB = rowsB[0]["secret_col"]?.ToString();
        pseudoB.ShouldNotBeNull();

        // Assert: Tenants produce different pseudonyms
        pseudoA.ShouldNotBe(pseudoB);
    }

    [Fact]
    public async Task LakehouseIcebergExecutor_ProducesDistinctPseudonymsPerTenant_AndSetsInDbMaskingExecuted()
    {
        var metaReader = Substitute.For<IIcebergMetadataReader>();
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);

        var icebergTableMeta = new IcebergTableMetadata(
            "uuid-1", 2, "s3://lake/orders", 1, 1000, 101,
            new IcebergSchema(0, [new IcebergField(1, "tenantId", "string", true), new IcebergField(2, "customerEmail", "string", true)]),
            new IcebergPartitionSpec(0, [new IcebergPartitionField(1, 1000, "tenantId", "identity")]),
            [new IcebergSnapshot(101, 1000, "manifest-list.json")]
        );

        metaReader.LoadTableMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IcebergTableMetadata>(icebergTableMeta));

        var files = new List<IcebergDataFile>
        {
            new("file1.parquet", "PARQUET", new Dictionary<string, string> { ["tenantId"] = "tenant-alpha" }, 10, 1024),
            new("file2.parquet", "PARQUET", new Dictionary<string, string> { ["tenantId"] = "tenant-beta" }, 10, 1024)
        };

        metaReader.LoadDataFilesAsync(Arg.Any<IcebergTableMetadata>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<IcebergDataFile>>(files));

        var executor = new LakehouseDataSourceExecutor(
            metaReader,
            pruner,
            _maskingProvider,
            _gatewayOptions,
            NullLogger<LakehouseDataSourceExecutor>.Instance,
            new DemoDataSwitch(true));

        var tableId = new TableIdentifier("lake", "sales", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns =
            [
                new TableColumn { ColumnName = "tenantId", DataType = "string" },
                new TableColumn { ColumnName = "customerEmail", DataType = "string", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["customerEmail"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        // Tenant Alpha
        var contextAlpha = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["tenantId"] = ColumnAccessLevel.Clear,
                ["customerEmail"] = ColumnAccessLevel.Mask
            }),
            new Dictionary<string, object?>(),
            new List<string> { "tenantId", "customerEmail" },
            null,
            1000,
            0,
            new TenantId("tenant-alpha"));

        var rowsAlpha = await executor.ExecuteAsync(contextAlpha);
        contextAlpha.Items.TryGetValue("InDbColumnMaskingExecuted", out var maskedAlpha).ShouldBeTrue();
        maskedAlpha.ShouldBe(true);
        rowsAlpha.Count.ShouldBeGreaterThan(0);
        var pseudoAlpha = rowsAlpha[0]["customerEmail"]?.ToString();
        pseudoAlpha.ShouldNotBeNull();

        // Tenant Beta
        var contextBeta = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
            {
                ["tenantId"] = ColumnAccessLevel.Clear,
                ["customerEmail"] = ColumnAccessLevel.Mask
            }),
            new Dictionary<string, object?>(),
            new List<string> { "tenantId", "customerEmail" },
            null,
            1000,
            0,
            new TenantId("tenant-beta"));

        var rowsBeta = await executor.ExecuteAsync(contextBeta);
        contextBeta.Items.TryGetValue("InDbColumnMaskingExecuted", out var maskedBeta).ShouldBeTrue();
        maskedBeta.ShouldBe(true);
        rowsBeta.Count.ShouldBeGreaterThan(0);
        var pseudoBeta = rowsBeta[0]["customerEmail"]?.ToString();
        pseudoBeta.ShouldNotBeNull();

        pseudoAlpha.ShouldNotBe(pseudoBeta);
    }

    [Fact]
    public void GovernedConnectorReader_WhenInDbMaskingExecutedIsTrue_DoesNotDoubleMask()
    {
        var tableId = new TableIdentifier("lake", "sales", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "sales", TableName = "orders" },
            Columns =
            [
                new TableColumn { ColumnName = "customerEmail", DataType = "string", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["customerEmail"] = new MaskingRule { RuleType = "HMAC" }
            }
        };

        var decision = TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>
        {
            ["customerEmail"] = ColumnAccessLevel.Mask
        });

        const string alreadyHmacMaskedValue = "4B71D57F138E619E4F98E90123456789";
        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["customerEmail"] = alreadyHmacMaskedValue }
        };

        var policy = new GovernedRowPolicy(
            _maskingProvider,
            HmacKeyId: "master-key");

        // Act with inDbMaskingExecuted = true
        var resultWithInDb = GovernedConnectorReader.Apply(
            rawRows,
            metadata,
            decision,
            tenantId: "tenant-alpha",
            rlsPushdownExecuted: false,
            inDbMaskingExecuted: true,
            policy: policy);

        // Value must NOT be double-masked
        resultWithInDb.Count.ShouldBe(1);
        resultWithInDb[0]["customerEmail"].ShouldBe(alreadyHmacMaskedValue);

        // Contrast: if inDbMaskingExecuted was false, it would hash again (different value)
        var resultWithoutInDb = GovernedConnectorReader.Apply(
            rawRows,
            metadata,
            decision,
            tenantId: "tenant-alpha",
            rlsPushdownExecuted: false,
            inDbMaskingExecuted: false,
            policy: policy);

        resultWithoutInDb.Count.ShouldBe(1);
        resultWithoutInDb[0]["customerEmail"].ShouldNotBe(alreadyHmacMaskedValue);
    }
}
