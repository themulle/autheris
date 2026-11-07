namespace Autheris.Tests.Unit.Performance;

using System;
using System.IO;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Hardware;
using Shouldly;
using Xunit;

public sealed class ContainerMemoryDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public ContainerMemoryDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "autheris_cgroup_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void ResolveContainerMemoryLimitBytes_ReadsCgroupV2()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        File.WriteAllText(v2Path, "2147483648\n"); // 2 GB

        var bytes = ContainerMemoryDetector.ReadCgroupLimitBytes(v2Path, v1Path);

        bytes.ShouldBe(2147483648L);
    }

    [Fact]
    public void ReadCgroupLimitBytes_WhenV2IsMax_FallsBackToV1()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        File.WriteAllText(v2Path, "max\n");
        File.WriteAllText(v1Path, "1073741824\n"); // 1 GB

        var bytes = ContainerMemoryDetector.ReadCgroupLimitBytes(v2Path, v1Path);

        bytes.ShouldBe(1073741824L);
    }

    [Fact]
    public void ReadCgroupLimitBytes_WhenV1IsUnlimited_ReturnsNull()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        File.WriteAllText(v2Path, "max\n");
        File.WriteAllText(v1Path, "9223372036854771712\n"); // cgroup v1 unlimited marker

        var bytes = ContainerMemoryDetector.ReadCgroupLimitBytes(v2Path, v1Path);

        bytes.ShouldBeNull();
    }

    [Fact]
    public void ReadCgroupLimitBytes_WhenFilesDoNotExist_ReturnsNull()
    {
        var v2Path = Path.Combine(_tempDir, "non_existent_v2");
        var v1Path = Path.Combine(_tempDir, "non_existent_v1");

        var bytes = ContainerMemoryDetector.ReadCgroupLimitBytes(v2Path, v1Path);

        bytes.ShouldBeNull();
    }
}
