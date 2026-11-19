namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Verifies at startup that the module root directory and individual module directories
/// are not writable in production environments (nor group/world-writable).
/// </summary>
public static class ModuleDirectoryPermissionCheck
{
    /// <summary>
    /// Verifies that <paramref name="directoryPath"/> satisfies directory write-protection rules.
    /// In production, the directory must not be writable by the gateway user, group, or others.
    /// </summary>
    public static void VerifyDirectory(
        string directoryPath,
        bool isProduction,
        string moduleId = "host",
        Func<string, bool>? isWritableOverride = null,
        Func<string, uint?>? ownerUidProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(fullPath))
        {
            throw new ModuleLoadException(moduleId, $"Module directory '{fullPath}' does not exist.");
        }

        if (!isProduction)
        {
            return;
        }

        VerifyNotSymlink(fullPath, moduleId);
        VerifyRootOwned(fullPath, moduleId, ownerUidProbe);

        if (isWritableOverride is not null)
        {
            if (isWritableOverride(fullPath))
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Module directory '{fullPath}' is writable in production; module directories must be read-only (0555/0444).");
            }

            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var mode = File.GetUnixFileMode(fullPath);
                const UnixFileMode writableMask =
                    UnixFileMode.UserWrite |
                    UnixFileMode.GroupWrite |
                    UnixFileMode.OtherWrite;

                if ((mode & writableMask) != 0)
                {
                    throw new ModuleLoadException(
                        moduleId,
                        $"Module directory '{fullPath}' has writable permission bits ({mode}) in production; expected read-only (0555/0444).");
                }

                return;
            }
            catch (ModuleLoadException)
            {
                throw;
            }
            catch (PlatformNotSupportedException)
            {
                // Fall back to attributes check below when UnixFileMode is unsupported by the underlying filesystem.
            }
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            throw new ModuleLoadException(
                moduleId,
                $"Module directory '{fullPath}' is writable in production; expected read-only directory attributes.");
        }
    }

    /// <summary>
    /// In production, every file listed by a module manifest must not carry group or other write bits.
    /// </summary>
    public static void VerifyFiles(
        string moduleDirectory,
        IEnumerable<string> relativePaths,
        bool isProduction,
        string moduleId,
        Func<string, bool>? isWritableOverride = null)
    {
        if (!isProduction)
        {
            return;
        }

        foreach (var relative in relativePaths)
        {
            var filePath = Path.GetFullPath(Path.Combine(moduleDirectory, relative));
            if (!File.Exists(filePath))
            {
                continue;
            }

            bool writable;
            if (isWritableOverride is not null)
            {
                writable = isWritableOverride(filePath);
            }
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const UnixFileMode groupOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
                writable = (File.GetUnixFileMode(filePath) & groupOtherWrite) != 0;
            }
            else
            {
                writable = false;
            }

            if (writable)
            {
                throw new ModuleLoadException(
                    moduleId,
                    $"Module file '{filePath}' is writable by group or others in production; expected read-only (0444).");
            }
        }
    }

    private static void VerifyNotSymlink(string fullPath, string moduleId)
    {
        if (new DirectoryInfo(fullPath).LinkTarget is not null)
        {
            throw new ModuleLoadException(
                moduleId,
                $"Module directory '{fullPath}' is a symbolic link; symlinked module directories are not allowed in production.");
        }
    }

    private static void VerifyRootOwned(string fullPath, string moduleId, Func<string, uint?>? ownerUidProbe)
    {
        // Ownership cannot be read portably from managed code; the probe is injected where a platform stat is available.
        if (ownerUidProbe is null)
        {
            return;
        }

        var owner = ownerUidProbe(fullPath);
        if (owner is not 0u)
        {
            throw new ModuleLoadException(
                moduleId,
                $"Module directory '{fullPath}' is not owned by root in production.");
        }
    }
}
