namespace Autheris.Infrastructure.Hardware;

using System;
using System.IO;

/// <summary>
/// Detects Linux container memory limits (cgroup v1/v2).
/// </summary>
public static class ContainerMemoryDetector
{
    public const string DefaultCgroupV2MemoryMaxPath = "/sys/fs/cgroup/memory.max";
    public const string DefaultCgroupV1MemoryLimitPath = "/sys/fs/cgroup/memory/memory.limit_in_bytes";

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
