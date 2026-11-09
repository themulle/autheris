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
    /// <summary>Upper bound for a single file inside a module folder.</summary>
    public const long MaxModuleFileBytes = 64L * 1024 * 1024;

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

            EnsureRegularFile(filePath, manifest.Id);

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
            byte[] fileBytes;
            using (var fileStream = new FileStream(fullFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileBytes = ReadCapped(fileStream, relPath, MaxModuleFileBytes);
            }

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

    /// <summary>
    /// Reads <paramref name="stream"/> completely but rejects the file as soon as more than
    /// <paramref name="maxBytes"/> bytes have actually been read, regardless of the reported length.
    /// </summary>
    internal static byte[] ReadCapped(Stream stream, string relativePath, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek && stream.Length <= maxBytes)
        {
            var exact = new byte[Math.Max(stream.Length, 0)];
            var filled = 0;
            int n;
            while (filled < exact.Length && (n = stream.Read(exact, filled, exact.Length - filled)) > 0)
            {
                filled += n;
            }

            // Anything beyond the reported length is read with the cap enforced on the bytes actually read.
            var probe = new byte[1];
            if (filled < exact.Length || stream.Read(probe, 0, 1) == 0)
            {
                return filled == exact.Length ? exact : exact[..filled];
            }

            using var rest = new MemoryStream();
            rest.Write(exact, 0, filled);
            rest.Write(probe, 0, 1);
            return ReadRemainder(stream, rest, relativePath, maxBytes);
        }

        using var buffer = new MemoryStream();
        return ReadRemainder(stream, buffer, relativePath, maxBytes);
    }

    private static byte[] ReadRemainder(Stream stream, MemoryStream buffer, string relativePath, long maxBytes)
    {
        var chunk = new byte[81920];
        long total = buffer.Length;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new SecurityException($"Module file '{relativePath}' exceeds the maximum size of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void EnsureRegularFile(string filePath, string moduleId)
    {
        var info = new FileInfo(filePath);
        if ((info.Attributes & (FileAttributes.Device | FileAttributes.Directory)) != 0)
        {
            throw new SecurityException($"Module '{moduleId}' contains a non-regular file entry.");
        }

        // Links were already bounded to the module root by EnsureNoSymlinkEscape; the kind of the final target decides.
        var kind = UnixFileStat.TryGetFileKind(filePath);
        if (kind is { } k && k != UnixFileKind.Regular)
        {
            throw new SecurityException($"Module '{moduleId}' contains a non-regular file entry.");
        }

        EnsureSize(info);
    }

    private static void EnsureSize(FileInfo info)
    {
        if (info.Length > MaxModuleFileBytes)
        {
            throw new SecurityException($"Module file '{info.Name}' exceeds the maximum size of {MaxModuleFileBytes} bytes.");
        }
    }

    public static void EnsureNoSymlinkEscape(string fullRoot, string candidatePath)
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
