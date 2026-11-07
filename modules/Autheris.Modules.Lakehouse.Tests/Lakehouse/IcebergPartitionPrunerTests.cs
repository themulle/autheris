namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class IcebergPartitionPrunerTests
{
    [Fact]
    public void PruneDataFiles_WithDatePredicate_ShouldPruneNonMatchingFiles()
    {
        // Arrange
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);
        var spec = new IcebergPartitionSpec(0, [new IcebergPartitionField(1, 1000, "orderDate", "identity")]);

        var files = new List<IcebergDataFile>
        {
            new("f1.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-06-01" }, 1000, 1024),
            new("f2.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-06-15" }, 1000, 1024),
            new("f3.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-05-10" }, 1000, 1024),
            new("f4.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-04-20" }, 1000, 1024),
            new("f5.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-03-01" }, 1000, 1024),
            new("f6.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-02-15" }, 1000, 1024),
            new("f7.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2026-01-10" }, 1000, 1024),
            new("f8.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2025-12-01" }, 1000, 1024),
            new("f9.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2025-11-01" }, 1000, 1024),
            new("f10.parquet", "PARQUET", new Dictionary<string, string> { ["orderDate"] = "2025-10-01" }, 1000, 1024)
        };

        var filters = new Dictionary<string, string>
        {
            ["orderDate"] = ">= 2026-06-01"
        };

        // Act
        var matched = pruner.PruneDataFiles(files, filters, spec);

        // Assert
        // Out of 10 files, exactly 2 (f1, f2) match June 2026 or later (80% pruned)
        matched.Count.ShouldBe(2);
        matched.ShouldContain(f => f.FilePath == "f1.parquet");
        matched.ShouldContain(f => f.FilePath == "f2.parquet");
        matched.ShouldNotContain(f => f.FilePath == "f3.parquet");
    }

    [Fact]
    public void PruneDataFiles_WithTenantAndBounds_ShouldAchieveHighEfficiency()
    {
        // Arrange
        var pruner = new IcebergPartitionPruner(NullLogger<IcebergPartitionPruner>.Instance);
        var spec = new IcebergPartitionSpec(0, [new IcebergPartitionField(1, 1000, "tenantId", "identity")]);

        var files = new List<IcebergDataFile>();
        // Create 10 files for different tenants
        for (int i = 1; i <= 10; i++)
        {
            files.Add(new IcebergDataFile(
                $"tenant_{i}.parquet",
                "PARQUET",
                new Dictionary<string, string> { ["tenantId"] = $"tenant-{i}" },
                5000,
                2048
            ));
        }

        var filters = new Dictionary<string, string>
        {
            ["tenantId"] = "== tenant-3"
        };

        // Act
        var matched = pruner.PruneDataFiles(files, filters, spec);

        // Assert
        // Exactly 1 file matches tenant-3 (90% pruned)
        matched.Count.ShouldBe(1);
        matched[0].FilePath.ShouldBe("tenant_3.parquet");
    }
}
