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

    [Fact]
    public void ResolveGarnetMemoryLimitMb_ExplicitOptionOverridesAll()
    {
        var mb = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(configuredMemorySizeMb: 1024);

        mb.ShouldBe(1024);
    }

    [Fact]
    public void ResolveGarnetMemoryLimitMb_Calculates25Percent()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        // 2048 MB = 2147483648 bytes -> 25% is 512 MB
        File.WriteAllText(v2Path, "2147483648\n");

        var mb = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(null, v2Path, v1Path);

        mb.ShouldBe(512);
    }

    [Fact]
    public void ResolveGarnetMemoryLimitMb_ClampsToMinimum64Mb()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        // 100 MB = 104857600 bytes -> 25% is 25 MB -> clamped to 64 MB
        File.WriteAllText(v2Path, "104857600\n");

        var mb = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(null, v2Path, v1Path);

        mb.ShouldBe(64);
    }

    [Fact]
    public void ResolveGarnetMemoryLimitMb_ClampsToMaximum4096Mb()
    {
        var v2Path = Path.Combine(_tempDir, "memory.max");
        var v1Path = Path.Combine(_tempDir, "memory.limit_in_bytes");
        // 32 GB = 34359738368 bytes -> 25% is 8192 MB -> clamped to 4096 MB
        File.WriteAllText(v2Path, "34359738368\n");

        var mb = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(null, v2Path, v1Path);

        mb.ShouldBe(4096);
    }
}
