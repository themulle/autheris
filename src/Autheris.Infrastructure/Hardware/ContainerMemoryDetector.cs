namespace Autheris.Infrastructure.Hardware;

using System;
using System.IO;

/// <summary>
/// Detects Linux container memory limits (cgroup v1/v2) and derives the memory ceiling for the embedded Garnet cache.
/// SEC-PERF254-07: Clamps derived limit between 64MB and 4096MB, never exceeding 25% of the container limit.
/// </summary>
public static class ContainerMemoryDetector
{
    public const string DefaultCgroupV2MemoryMaxPath = "/sys/fs/cgroup/memory.max";
    public const string DefaultCgroupV1MemoryLimitPath = "/sys/fs/cgroup/memory/memory.limit_in_bytes";

    /// <summary>
    /// Resolves the effective memory limit in MB for the embedded Garnet cache.
    /// Priority:
    /// 1. Explicitly configured MemorySizeMb (clamped 64MB - 65536MB).
    /// 2. Linux cgroup limit (v2 or v1) * 25%, clamped between 64MB and 4096MB.
    /// 3. .NET GC TotalAvailableMemoryBytes * 25%, clamped between 64MB and 4096MB.
    /// 4. Default fallback: 512MB.
    /// </summary>
    public static int ResolveGarnetMemoryLimitMb(
        int? configuredMemorySizeMb,
        string cgroupV2Path = DefaultCgroupV2MemoryMaxPath,
        string cgroupV1Path = DefaultCgroupV1MemoryLimitPath,
        Func<long>? gcMemoryInfoProvider = null)
    {
        if (configuredMemorySizeMb.HasValue)
        {
            return Math.Clamp(configuredMemorySizeMb.Value, 64, 65536);
        }

        long? containerLimitBytes = ReadCgroupLimitBytes(cgroupV2Path, cgroupV1Path);
        if (!containerLimitBytes.HasValue || containerLimitBytes.Value <= 0)
        {
            long gcBytes = gcMemoryInfoProvider != null ? gcMemoryInfoProvider() : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (gcBytes > 0 && gcBytes < long.MaxValue / 2)
            {
                containerLimitBytes = gcBytes;
            }
        }

        if (containerLimitBytes.HasValue && containerLimitBytes.Value > 0)
        {
            long quarterBytes = containerLimitBytes.Value / 4;
            int quarterMb = (int)(quarterBytes / (1024 * 1024));
            return Math.Clamp(quarterMb, 64, 4096);
        }

        return 512;
    }

    public static long? ReadCgroupLimitBytes(string cgroupV2Path = DefaultCgroupV2MemoryMaxPath, string cgroupV1Path = DefaultCgroupV1MemoryLimitPath)
    {
        try
        {
            // Cgroup v2
            if (File.Exists(cgroupV2Path))
            {
                var text = File.ReadAllText(cgroupV2Path).Trim();
                // "max" indicates no limit set
                if (!string.Equals(text, "max", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(text, out var v2Bytes) && v2Bytes > 0)
                {
                    return v2Bytes;
                }
            }

            // Cgroup v1
            if (File.Exists(cgroupV1Path))
            {
                var text = File.ReadAllText(cgroupV1Path).Trim();
                // 0x7FFFFFFFFFFFF000 is Linux kernel's default unset limit constant
                if (long.TryParse(text, out var v1Bytes) && v1Bytes > 0 && v1Bytes < 0x7FFFFFFFFFFFF000L)
                {
                    return v1Bytes;
                }
            }
        }
        catch
        {
            // Fall back gracefully to runtime GC memory info
        }

        return null;
    }
}
