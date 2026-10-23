namespace Autheris.Application.Olap;

using System;
using System.Globalization;
using System.IO;

/// <summary>
/// MISC-05: the memory limit of the container (cgroup v2 <c>memory.max</c>, then v1 <c>memory/memory.limit_in_bytes</c>).
/// <c>max</c>, an unreadable file, or an effectively unlimited v1 value means "no limit" (null).
/// </summary>
public static class ContainerMemoryLimit
{
    private const string CgroupV2Path = "/sys/fs/cgroup/memory.max";
    private const string CgroupV1Path = "/sys/fs/cgroup/memory/memory.limit_in_bytes";

    /// <summary>Anything at or above 2^60 bytes is treated as unlimited (cgroup v1 reports a page-rounded huge value).</summary>
    private const long UnlimitedThreshold = 1L << 60;

    public static long? Read(Func<string, string?>? readFile = null)
    {
        readFile ??= DefaultRead;
        return Parse(readFile(CgroupV2Path)) ?? Parse(readFile(CgroupV1Path));
    }

    private static long? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "max", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value < UnlimitedThreshold
            ? value
            : null;
    }

    private static string? DefaultRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
