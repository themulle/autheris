namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Runtime.InteropServices;

/// <summary>File kinds distinguished by <see cref="UnixFileStat"/>.</summary>
public enum UnixFileKind
{
    Regular,
    Directory,
    Symlink,
    Fifo,
    Socket,
    CharacterDevice,
    BlockDevice,
    Unknown,
}

/// <summary>
/// Minimal Linux <c>statx(2)</c> wrapper used to read the file type and owner without opening the file
/// (opening a FIFO would block). Returns <see langword="null"/> where <c>statx</c> is unavailable
/// (Windows, non-Linux Unix); callers then rely on the portable checks.
/// </summary>
public static partial class UnixFileStat
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxBufferSize = 256;
    private const int UidOffset = 20;
    private const int ModeOffset = 28;

    private const uint StatxType = 0x1;
    private const int Enoent = 2;
    private const int Enotdir = 20;

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatxNative(int dirfd, string path, int flags, uint mask, byte* buffer);

    public static UnixFileKind? TryGetFileKind(string path, bool followLinks = true)
    {
        var info = TryStat(path, followLinks);
        if (info is null)
        {
            return null;
        }

        if (info.Value.Failed)
        {
            return UnixFileKind.Unknown;
        }

        return ClassifyMode(info.Value.Mode);
    }

    /// <summary>
    /// Maps the outcome of a <c>statx</c> call to a kind. A failed call on an existing path, or a result whose
    /// type bits were not filled in, is <see cref="UnixFileKind.Unknown"/> so callers fail closed.
    /// </summary>
    internal static UnixFileKind? ClassifyStatxResult(int result, int errno, uint mask, uint mode)
    {
        if (result != 0)
        {
            return errno is Enoent or Enotdir ? null : UnixFileKind.Unknown;
        }

        return (mask & StatxType) == 0 ? UnixFileKind.Unknown : ClassifyMode(mode);
    }

    private static UnixFileKind ClassifyMode(uint mode)
    {
        return (mode & 0xF000) switch
        {
            0x8000 => UnixFileKind.Regular,
            0x4000 => UnixFileKind.Directory,
            0xA000 => UnixFileKind.Symlink,
            0x1000 => UnixFileKind.Fifo,
            0xC000 => UnixFileKind.Socket,
            0x2000 => UnixFileKind.CharacterDevice,
            0x6000 => UnixFileKind.BlockDevice,
            _ => UnixFileKind.Unknown,
        };
    }

    /// <summary>Returns the numeric owner of the path itself (links are not followed), or <see langword="null"/> when unknown.</summary>
    public static uint? TryGetOwnerUid(string path) => TryStat(path, followLinks: false)is { Failed: false } i ? i.Uid : null;

    /// <summary>Returns the effective user id of the process, or <see langword="null"/> when unavailable.</summary>
    public static uint? TryGetEffectiveUid()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            return GetEuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    private static unsafe (uint Uid, uint Mode, bool Failed)? TryStat(string path, bool followLinks)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        byte* buffer = stackalloc byte[StatxBufferSize];
        try
        {
            var flags = followLinks ? 0 : AtSymlinkNoFollow;
            var result = StatxNative(AtFdCwd, path, flags, StatxBasicStats, buffer);
            var kind = ClassifyStatxResult(result, result == 0 ? 0 : Marshal.GetLastPInvokeError(), *(uint*)buffer, (uint)*(ushort*)(buffer + ModeOffset));
            if (kind is null)
            {
                return null;
            }

            if (kind == UnixFileKind.Unknown)
            {
                return (0, 0, true);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        var uid = *(uint*)(buffer + UidOffset);
        var mode = (uint)*(ushort*)(buffer + ModeOffset);
        return (uid, mode, false);
    }
}
