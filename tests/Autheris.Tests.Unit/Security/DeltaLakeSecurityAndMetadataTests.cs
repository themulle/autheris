namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DeltaLakeSecurityAndMetadataTests
{
    private readonly ILakehouseStorageProvider _storageProvider = Substitute.For<ILakehouseStorageProvider>();

    [Fact]
    public async Task LoadSnapshot_ReconstructsActiveFilesFromCommitLogsCorrectly()
    {
        // Arrange
        const string tableLocation = "lakehouse/delta/sales";

        // Commit 0: add file1.parquet and file2.parquet
        var commit0Json =
            "{\"protocol\":{\"minReaderVersion\":1,\"minWriterVersion\":2}}\n" +
            "{\"metaData\":{\"id\":\"sales-uuid-1\",\"name\":\"sales\",\"schemaString\":\"{\\\"type\\\":\\\"struct\\\",\\\"fields\\\":[{\\\"name\\\":\\\"id\\\",\\\"type\\\":\\\"string\\\"},{\\\"name\\\":\\\"region\\\",\\\"type\\\":\\\"string\\\"}]}\",\"partitionColumns\":[\"region\"]}}\n" +
            "{\"add\":{\"path\":\"region=EU/file1.parquet\",\"partitionValues\":{\"region\":\"EU\"},\"size\":1024,\"modificationTime\":1700000000000,\"dataChange\":true}}\n" +
            "{\"add\":{\"path\":\"region=US/file2.parquet\",\"partitionValues\":{\"region\":\"US\"},\"size\":2048,\"modificationTime\":1700000000000,\"dataChange\":true}}";

        // Commit 1: remove file1.parquet, add file3.parquet
        var commit1Json =
            "{\"remove\":{\"path\":\"region=EU/file1.parquet\",\"deletionTimestamp\":1700000100000,\"dataChange\":true}}\n" +
            "{\"add\":{\"path\":\"region=EU/file3.parquet\",\"partitionValues\":{\"region\":\"EU\"},\"size\":1536,\"modificationTime\":1700000100000,\"dataChange\":true}}";

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        _storageProvider.ReadTextAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(commit0Json));

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000001.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        _storageProvider.ReadTextAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000001.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(commit1Json));

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000002.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var reader = new DeltaMetadataReader(_storageProvider, NullLogger<DeltaMetadataReader>.Instance);

        // Act
        var snapshot = await reader.LoadSnapshotAsync(tableLocation);

        // Assert
        snapshot.Version.ShouldBe(1);
        snapshot.Metadata.Name.ShouldBe("sales");
        snapshot.Metadata.PartitionColumns.ShouldContain("region");
        snapshot.ActiveFiles.Count.ShouldBe(2);
        snapshot.ActiveFiles.ShouldContain(f => f.Path == "region=US/file2.parquet");
        snapshot.ActiveFiles.ShouldContain(f => f.Path == "region=EU/file3.parquet");
        snapshot.ActiveFiles.ShouldNotContain(f => f.Path == "region=EU/file1.parquet");
    }

    [Fact]
    public async Task TimeTravel_LoadsSnapshotAsOfVersion0()
    {
        // Arrange
        const string tableLocation = "lakehouse/delta/sales";

        var commit0Json =
            "{\"protocol\":{\"minReaderVersion\":1,\"minWriterVersion\":2}}\n" +
            "{\"metaData\":{\"id\":\"sales-uuid-1\",\"name\":\"sales\",\"schemaString\":\"{\\\"type\\\":\\\"struct\\\",\\\"fields\\\":[{\\\"name\\\":\\\"id\\\",\\\"type\\\":\\\"string\\\"}]}\",\"partitionColumns\":[]}}\n" +
            "{\"add\":{\"path\":\"file1.parquet\",\"partitionValues\":{},\"size\":1024,\"modificationTime\":1700000000000,\"dataChange\":true}}";

        var commit1Json =
            "{\"add\":{\"path\":\"file2.parquet\",\"partitionValues\":{},\"size\":2048,\"modificationTime\":1700000100000,\"dataChange\":true}}";

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        _storageProvider.ReadTextAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(commit0Json));

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000001.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        _storageProvider.ReadTextAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000001.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(commit1Json));

        var reader = new DeltaMetadataReader(_storageProvider, NullLogger<DeltaMetadataReader>.Instance);

        // Act - Time travel to version 0
        var snapshotV0 = await reader.LoadSnapshotAsync(tableLocation, asOfVersion: 0);

        // Assert
        snapshotV0.Version.ShouldBe(0);
        snapshotV0.ActiveFiles.Count.ShouldBe(1);
        snapshotV0.ActiveFiles.ShouldContain(f => f.Path == "file1.parquet");
        snapshotV0.ActiveFiles.ShouldNotContain(f => f.Path == "file2.parquet");
    }

    [Fact]
    public async Task PathTraversalInDeltaLog_ThrowsSecurityException()
    {
        // Arrange
        const string tableLocation = "lakehouse/delta/finance";

        var maliciousCommit =
            "{\"protocol\":{\"minReaderVersion\":1,\"minWriterVersion\":2}}\n" +
            "{\"metaData\":{\"id\":\"fin-1\",\"name\":\"fin\",\"schemaString\":\"{\\\"type\\\":\\\"struct\\\",\\\"fields\\\":[]}\",\"partitionColumns\":[]}}\n" +
            "{\"add\":{\"path\":\"../../etc/shadow\",\"partitionValues\":{},\"size\":512,\"modificationTime\":1700000000000,\"dataChange\":true}}";

        _storageProvider.ExistsAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
        _storageProvider.ReadTextAsync(Arg.Is<string>(s => s.EndsWith("00000000000000000000.json")), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(maliciousCommit));

        var reader = new DeltaMetadataReader(_storageProvider, NullLogger<DeltaMetadataReader>.Instance);

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await reader.LoadSnapshotAsync(tableLocation);
        });
    }

    [Fact]
    public void PartitionPruner_SkipsFilesNotMatchingPartitionOrBounds()
    {
        // Arrange
        var pruner = new DeltaPartitionPruner(NullLogger<DeltaPartitionPruner>.Instance);

        var file1 = new DeltaDataFile(
            Path: "region=EU/data1.parquet",
            PartitionValues: new Dictionary<string, string> { ["region"] = "EU" },
            SizeBytes: 1000,
            ModificationTimeMs: 1700000000000,
            DataChange: true,
            MinValues: new Dictionary<string, string> { ["age"] = "18" },
            MaxValues: new Dictionary<string, string> { ["age"] = "30" });

        var file2 = new DeltaDataFile(
            Path: "region=US/data2.parquet",
            PartitionValues: new Dictionary<string, string> { ["region"] = "US" },
            SizeBytes: 1000,
            ModificationTimeMs: 1700000000000,
            DataChange: true,
            MinValues: new Dictionary<string, string> { ["age"] = "31" },
            MaxValues: new Dictionary<string, string> { ["age"] = "65" });

        var allFiles = new List<DeltaDataFile> { file1, file2 };
        var partitionCols = new List<string> { "region" };

        // Act 1: Partition filter on region=EU
        var prunedByRegion = pruner.PruneDataFiles(allFiles, partitionCols, new Dictionary<string, string> { ["region"] = "EU" });
        prunedByRegion.Count.ShouldBe(1);
        prunedByRegion.ShouldContain(file1);

        // Act 2: Min/Max bound skipping for age=50 (should prune file1 since max age is 30)
        var prunedByAge = pruner.PruneDataFiles(allFiles, partitionCols, new Dictionary<string, string> { ["age"] = "50" });
        prunedByAge.Count.ShouldBe(1);
        prunedByAge.ShouldContain(file2);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTenant_FailsClosed()
    {
        // Arrange
        var metaReader = Substitute.For<IDeltaMetadataReader>();
        var pruner = Substitute.For<IDeltaPartitionPruner>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

        var executor = new DeltaLakeDataSourceExecutor(metaReader, pruner, masking, options, NullLogger<DeltaLakeDataSourceExecutor>.Instance);

        var tableId = new TableIdentifier("lake", "default", "delta_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "default", TableName = "delta_table", DataSourceType = DataSourceType.LakehouseDelta },
            Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var context = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }),
            new Dictionary<string, object?>(),
            new List<string> { "id" },
            null,
            1000,
            0,
            null); // Missing tenant context

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedAccess_FailsClosed()
    {
        // Arrange
        var metaReader = Substitute.For<IDeltaMetadataReader>();
        var pruner = Substitute.For<IDeltaPartitionPruner>();
        var masking = Substitute.For<IColumnMaskingProvider>();
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

        var executor = new DeltaLakeDataSourceExecutor(metaReader, pruner, masking, options, NullLogger<DeltaLakeDataSourceExecutor>.Instance);

        var tableId = new TableIdentifier("lake", "default", "delta_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SourceName = "lake", SchemaName = "default", TableName = "delta_table", DataSourceType = DataSourceType.LakehouseDelta },
            Columns = [new TableColumn { ColumnName = "id", DataType = "string" }]
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var context = new DataSourceExecutionContext(
            "lake",
            metadata,
            principal,
            TableAccessDecision.Denied(tableId, "Policy violation"),
            new Dictionary<string, object?>(),
            new List<string> { "id" },
            null,
            1000,
            0,
            new TenantId("tenant-1"));

        // Act
        var result = await executor.ExecuteAsync(context);

        // Assert
        result.ShouldBeEmpty();
    }
}

