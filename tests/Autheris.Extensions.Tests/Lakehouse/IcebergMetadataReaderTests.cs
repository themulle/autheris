namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class IcebergMetadataReaderTests
{
    private sealed class InMemoryStorageProvider : ILakehouseStorageProvider
    {
        public string? MetadataJsonContent { get; set; }
        public string? ManifestListJsonContent { get; set; }

        public ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
        {
            if (location.Contains("metadata.json")) return ValueTask.FromResult(MetadataJsonContent ?? "{}");
            if (location.Contains("manifest-list")) return ValueTask.FromResult(ManifestListJsonContent ?? "{}");
            return ValueTask.FromResult("{}");
        }

        public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
        {
            var content = location.Contains("metadata.json") ? MetadataJsonContent : ManifestListJsonContent;
            return ValueTask.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content ?? "{}")));
        }

        public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task LoadTableMetadataAsync_ShouldParseIcebergV2MetadataCorrectly()
    {
        // Arrange
        var storage = new InMemoryStorageProvider
        {
            MetadataJsonContent = """
            {
              "format-version": 2,
              "table-uuid": "a4d3f86e-92b1-4f9e-a89c-d083b4007812",
              "location": "s3://analytics-lake/tables/orders",
              "last-sequence-number": 42,
              "last-updated-ms": 1774692000000,
              "current-schema-id": 0,
              "schemas": [
                {
                  "schema-id": 0,
                  "fields": [
                    { "id": 1, "name": "orderId", "type": "string", "required": true },
                    { "id": 2, "name": "tenantId", "type": "string", "required": true },
                    { "id": 3, "name": "customerEmail", "type": "string", "required": false },
                    { "id": 4, "name": "amount", "type": "double", "required": false },
                    { "id": 5, "name": "orderDate", "type": "string", "required": true }
                  ]
                }
              ],
              "default-spec-id": 0,
              "partition-specs": [
                {
                  "spec-id": 0,
                  "fields": [
                    { "source-id": 5, "field-id": 1000, "name": "orderDate", "transform": "identity" },
                    { "source-id": 2, "field-id": 1001, "name": "tenantId", "transform": "identity" }
                  ]
                }
              ],
              "current-snapshot-id": 801122334455,
              "snapshots": [
                {
                  "snapshot-id": 801122334455,
                  "timestamp-ms": 1774692000000,
                  "manifest-list": "s3://analytics-lake/tables/orders/metadata/snap-801122334455-manifest-list.json"
                }
              ]
            }
            """
        };

        var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);

        // Act
        var metadata = await reader.LoadTableMetadataAsync("s3://analytics-lake/tables/orders/metadata/v2.metadata.json");

        // Assert
        metadata.ShouldNotBeNull();
        metadata.FormatVersion.ShouldBe(2);
        metadata.TableUuid.ShouldBe("a4d3f86e-92b1-4f9e-a89c-d083b4007812");
        metadata.Location.ShouldBe("s3://analytics-lake/tables/orders");
        metadata.CurrentSnapshotId.ShouldBe(801122334455L);
        metadata.CurrentSchema.Fields.Count.ShouldBe(5);
        metadata.CurrentSchema.Fields[0].Name.ShouldBe("orderId");
        metadata.PartitionSpec.Fields.Count.ShouldBe(2);
        metadata.PartitionSpec.Fields[0].Name.ShouldBe("orderDate");
        metadata.Snapshots.Count.ShouldBe(1);
    }

    [Fact]
    public async Task LoadDataFilesAsync_ShouldParseManifestListAndFiles()
    {
        // Arrange
        var storage = new InMemoryStorageProvider
        {
            ManifestListJsonContent = """
            {
              "entries": [
                {
                  "file_path": "s3://analytics-lake/tables/orders/data/orderDate=2026-06-01/0001.parquet",
                  "file_format": "PARQUET",
                  "record_count": 5000,
                  "file_size_in_bytes": 1048576,
                  "partition": { "orderDate": "2026-06-01", "tenantId": "tenant-alpha" },
                  "lower_bounds": { "orderDate": "2026-06-01", "amount": "10.0" },
                  "upper_bounds": { "orderDate": "2026-06-01", "amount": "999.0" }
                },
                {
                  "file_path": "s3://analytics-lake/tables/orders/data/orderDate=2026-05-01/0002.parquet",
                  "file_format": "PARQUET",
                  "record_count": 4200,
                  "file_size_in_bytes": 838860,
                  "partition": { "orderDate": "2026-05-01", "tenantId": "tenant-beta" },
                  "lower_bounds": { "orderDate": "2026-05-01", "amount": "5.0" },
                  "upper_bounds": { "orderDate": "2026-05-01", "amount": "450.0" }
                }
              ]
            }
            """
        };

        var reader = new IcebergMetadataReader(storage, NullLogger<IcebergMetadataReader>.Instance);
        var metadata = new IcebergTableMetadata(
            "test-uuid",
            2,
            "s3://analytics-lake/tables/orders",
            1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            999L,
            new IcebergSchema(0, []),
            new IcebergPartitionSpec(0, []),
            [new IcebergSnapshot(999L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "s3://analytics-lake/tables/orders/metadata/snap-999-manifest-list.json")]
        );

        // Act
        var files = await reader.LoadDataFilesAsync(metadata);

        // Assert
        files.Count.ShouldBe(2);
        files[0].FilePath.ShouldContain("2026-06-01");
        files[0].PartitionValues["orderDate"].ShouldBe("2026-06-01");
        files[0].PartitionValues["tenantId"].ShouldBe("tenant-alpha");
        files[1].FilePath.ShouldContain("2026-05-01");
    }
}
