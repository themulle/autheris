namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Holds the in-memory byte buffers of a module directory after all files have been read once and verified.
/// Assembly loading operates strictly over these verified in-memory buffers to prevent TOCTOU swaps on disk.
/// </summary>
public sealed class VerifiedModuleImage
{
    public VerifiedModuleImage(
        string moduleId,
        string moduleDirectory,
        ModuleManifest manifest,
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        ModuleId = moduleId;
        ModuleDirectory = moduleDirectory;
        Manifest = manifest;
        Files = files;
    }

    public string ModuleId { get; }

    public string ModuleDirectory { get; }

    public ModuleManifest Manifest { get; }

    /// <summary>
    /// Verified file bytes keyed by normalized relative path with <c>/</c> separators.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    public bool TryGetFileBytes(string relativePath, [NotNullWhen(true)] out byte[]? bytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            bytes = null;
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        return Files.TryGetValue(normalized, out bytes);
    }

    public byte[] GetRequiredFileBytes(string relativePath)
    {
        if (!TryGetFileBytes(relativePath, out var bytes))
        {
            throw new InvalidOperationException($"Module '{ModuleId}' does not contain verified file '{relativePath}'.");
        }

        return bytes;
    }
}
