namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

/// <summary>
/// Reads every file in a module folder into memory once and verifies its SHA-256 digest against <see cref="ModuleManifest.Files"/>
/// using constant-time comparison. Fails closed on unlisted files, missing listed files, path traversal, or hash mismatch.
/// </summary>
public static class FileHashVerifier
{
    /// <summary>
    /// Verifies all files under <paramref name="moduleDirectory"/> against <paramref name="manifest"/> and returns
    /// an in-memory <see cref="VerifiedModuleImage"/>.
    /// </summary>
    public static VerifiedModuleImage VerifyAndLoad(
        string moduleDirectory,
        ModuleManifest manifest,
        bool verifyHashes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        var fullRoot = Path.GetFullPath(moduleDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Module directory '{fullRoot}' does not exist.");
        }

        EnsureNoSymlinkEscape(fullRoot, fullRoot);

        // Build expected map from manifest
        var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (!ModuleManifest.IsValidRelativeManifestPath(entry.Path))
            {
                throw new SecurityException($"Module '{manifest.Id}' manifest contains invalid path '{entry.Path}'.");
            }

            if (verifyHashes && !IntegrityHashHelper.IsLowerHexSha256(entry.Sha256))
            {
                throw new SecurityException($"Module '{manifest.Id}' manifest contains invalid SHA-256 hash for '{entry.Path}'.");
            }

            if (!expectedHashes.TryAdd(entry.Path, entry.Sha256))
            {
                throw new SecurityException($"Module '{manifest.Id}' manifest lists duplicate file '{entry.Path}'.");
            }
        }

        // Enumerate all subdirectories to check for symlink escapes
        foreach (var subDir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories))
        {
            EnsureNoSymlinkEscape(fullRoot, subDir);
        }

        var loadedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            EnsureNoSymlinkEscape(fullRoot, filePath);

            var fullFilePath = Path.GetFullPath(filePath);
            if (!fullFilePath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new SecurityException($"Module '{manifest.Id}' file '{filePath}' escapes module directory.");
            }

            var relPath = Path.GetRelativePath(fullRoot, fullFilePath).Replace('\\', '/');
            if (string.Equals(relPath, "module.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ModuleManifest.IsValidRelativeManifestPath(relPath))
            {
                throw new SecurityException($"Module '{manifest.Id}' contains invalid relative file path '{relPath}'.");
            }

            // Read the file into memory once
            byte[] fileBytes = File.ReadAllBytes(fullFilePath);

            if (verifyHashes)
            {
                if (!expectedHashes.TryGetValue(relPath, out var expectedHex))
                {
                    throw new SecurityException($"Module '{manifest.Id}' directory contains unlisted file '{relPath}'.");
                }

                byte[] actualHash = IntegrityHashHelper.ComputeSha256Bytes(fileBytes);
                if (!IntegrityHashHelper.FixedTimeEqualsHex(expectedHex, actualHash))
                {
                    throw new SecurityException($"Module '{manifest.Id}' file '{relPath}' failed SHA-256 integrity verification.");
                }
            }
            else if (expectedHashes.TryGetValue(relPath, out var expectedHex))
            {
                // Even in unsigned dev mode, if the manifest lists a hash, verify it matches if non-empty
                byte[] actualHash = IntegrityHashHelper.ComputeSha256Bytes(fileBytes);
                if (!IntegrityHashHelper.FixedTimeEqualsHex(expectedHex, actualHash))
                {
                    throw new SecurityException($"Module '{manifest.Id}' file '{relPath}' failed SHA-256 integrity verification.");
                }
            }

            loadedFiles[relPath] = fileBytes;
        }

        if (verifyHashes)
        {
            foreach (var expectedPath in expectedHashes.Keys)
            {
                if (!loadedFiles.ContainsKey(expectedPath))
                {
                    throw new SecurityException($"Module '{manifest.Id}' is missing listed file '{expectedPath}'.");
                }
            }
        }

        if (!loadedFiles.ContainsKey(manifest.EntryAssembly))
        {
            throw new SecurityException($"Module '{manifest.Id}' entry assembly '{manifest.EntryAssembly}' was not found in verified module image.");
        }

        return new VerifiedModuleImage(manifest.Id, fullRoot, manifest, loadedFiles);
    }

    private static void EnsureNoSymlinkEscape(string fullRoot, string candidatePath)
    {
        FileSystemInfo info = Directory.Exists(candidatePath)
            ? new DirectoryInfo(candidatePath)
            : new FileInfo(candidatePath);

        if (info.LinkTarget is not null)
        {
            var targetInfo = Directory.Exists(candidatePath)
                ? ((DirectoryInfo)info).ResolveLinkTarget(returnFinalTarget: true)
                : ((FileInfo)info).ResolveLinkTarget(returnFinalTarget: true);

            if (targetInfo is null)
            {
                throw new SecurityException($"Symlink '{candidatePath}' could not be resolved.");
            }

            var resolvedFull = Path.GetFullPath(targetInfo.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(resolvedFull, fullRoot, StringComparison.Ordinal) &&
                !resolvedFull.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new SecurityException($"Symlink '{candidatePath}' resolves outside module directory '{fullRoot}'.");
            }
        }
    }
}
