namespace Autheris.Api.Hosting;

using System;
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
        Func<string, bool>? isWritableOverride = null)
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
}
