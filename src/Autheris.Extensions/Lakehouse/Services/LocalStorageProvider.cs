namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Options;

/// <summary>
/// Storage provider for local filesystem or mounted persistent volumes (Kubernetes PVC, Docker bind-mounts).
/// </summary>
public sealed class LocalStorageProvider : ILakehouseStorageProvider
{
    private readonly string? _basePath;
    private readonly long _maxReadBytes;

    public LocalStorageProvider(
        IOptions<GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
    {
        var localPath = options?.Value.Lakehouse.Storage.LocalBasePath;
        _maxReadBytes = LakehouseLocationGuard.ResolveMaxReadBytes(options?.Value);

        if (!string.IsNullOrWhiteSpace(localPath))
        {
            _basePath = localPath;
        }
        else if (environment == null || IsDevelopment(environment))
        {
            // Development / unit-test fallback only
            _basePath = AppContext.BaseDirectory;
        }
        else
        {
            // SEC: LocalBasePath is mandatory outside Development (fail-closed on first use, not at startup)
            _basePath = null;
        }
    }

    public async ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var resolvedPath = ResolvePath(location);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Lakehouse file not found at '{resolvedPath}'.", resolvedPath);
        }

        var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > _maxReadBytes)
            {
                throw new InvalidDataException($"Lakehouse file '{location}' exceeds the maximum allowed size of {_maxReadBytes} bytes.");
            }

            return await LakehouseLocationGuard.ReadBoundedTextAsync(stream, _maxReadBytes, location, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var resolvedPath = ResolvePath(location);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Lakehouse file not found at '{resolvedPath}'.", resolvedPath);
        }

        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.Length > _maxReadBytes)
        {
            throw new InvalidDataException($"Lakehouse file '{location}' ({fileInfo.Length} bytes) exceeds the maximum allowed size of {_maxReadBytes} bytes.");
        }

        Stream stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)) return ValueTask.FromResult(false);
        var resolvedPath = ResolvePath(location);
        return ValueTask.FromResult(File.Exists(resolvedPath));
    }

    private static bool IsDevelopment(Microsoft.Extensions.Hosting.IHostEnvironment environment) =>
        string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    private string ResolvePath(string location)
    {
        if (_basePath == null)
        {
            throw new System.Security.SecurityException(
                "Lakehouse:Storage:LocalBasePath must be configured outside of the Development environment.");
        }

        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            location = location[7..];
        }

        if (location.Contains('\0'))
        {
            throw new System.Security.SecurityException("Lakehouse path must not contain null bytes.");
        }

        var fullBasePath = Path.GetFullPath(_basePath);
        var normalizedBase = fullBasePath.EndsWith(Path.DirectorySeparatorChar)
            ? fullBasePath
            : fullBasePath + Path.DirectorySeparatorChar;

        var combined = Path.IsPathRooted(location)
            ? Path.GetFullPath(location)
            : Path.GetFullPath(Path.Combine(fullBasePath, location));

        if (!combined.StartsWith(normalizedBase, StringComparison.Ordinal) &&
            !string.Equals(combined, fullBasePath, StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"Access to path outside configured lakehouse base directory is denied: '{location}'.");
        }

        EnsureNoSymbolicLinks(combined, fullBasePath, location);
        return combined;
    }

    /// <summary>
    /// SEC: Rejects symbolic links / reparse points on the path between the base directory and the target
    /// (a link inside the base directory could otherwise point outside of it).
    /// </summary>
    private static void EnsureNoSymbolicLinks(string combined, string fullBasePath, string location)
    {
        var current = combined;
        var trimmedBase = fullBasePath.TrimEnd(Path.DirectorySeparatorChar);

        while (!string.IsNullOrEmpty(current) &&
               current.Length > trimmedBase.Length &&
               current.StartsWith(trimmedBase, StringComparison.Ordinal))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new System.Security.SecurityException($"Symbolic links are not permitted in lakehouse locations: '{location}'.");
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }
}
