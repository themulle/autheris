namespace Autheris.Api.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autheris.Infrastructure.Modules.Trust;

/// <summary>
/// Non-collectible assembly load context for an external signed module.
/// Loads managed assemblies exclusively from the in-memory <see cref="VerifiedModuleImage"/> buffers
/// and delegates host/framework contracts to <see cref="AssemblyLoadContext.Default"/>.
/// </summary>
public sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly VerifiedModuleImage _image;
    private readonly ConcurrentDictionary<string, Assembly> _loadedBySimpleName = new(StringComparer.OrdinalIgnoreCase);

    public ModuleAssemblyLoadContext(VerifiedModuleImage image)
        : base(name: $"AutherisModule:{image?.ModuleId ?? "unknown"}", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
    }

    public VerifiedModuleImage Image => _image;

    /// <summary>
    /// Verifies that no managed assembly in the module package has a simple name that collides
    /// with any assembly loaded in <see cref="AssemblyLoadContext.Default"/> or with reserved host prefixes.
    /// </summary>
    public void EnsureNoHostAssemblyCollisions(IEnumerable<Assembly>? defaultContextAssemblies = null)
    {
        var hostAssemblies = (defaultContextAssemblies ?? Default.Assemblies)
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, bytes) in _image.Files)
        {
            if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (relativePath.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) &&
                relativePath.Contains("/native/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileSimpleName = Path.GetFileNameWithoutExtension(relativePath);
            if (IsCollidingWithHost(fileSimpleName, hostAssemblies))
            {
                throw new ModuleLoadException(
                    _image.ModuleId,
                    $"Module file '{relativePath}' collides with host assembly '{fileSimpleName}'.");
            }

            var embeddedName = TryReadManagedAssemblySimpleName(bytes);
            if (!string.IsNullOrEmpty(embeddedName) && IsCollidingWithHost(embeddedName, hostAssemblies))
            {
                throw new ModuleLoadException(
                    _image.ModuleId,
                    $"Module assembly '{relativePath}' declares assembly name '{embeddedName}' which collides with a host assembly.");
            }
        }
    }

    /// <summary>
    /// Loads the module's entry assembly strictly from the verified in-memory buffer.
    /// </summary>
    public Assembly LoadEntryAssembly()
    {
        byte[] entryBytes = _image.GetRequiredFileBytes(_image.Manifest.EntryAssembly);
        var simpleName = TryReadManagedAssemblySimpleName(entryBytes)
            ?? Path.GetFileNameWithoutExtension(_image.Manifest.EntryAssembly);

        return _loadedBySimpleName.GetOrAdd(simpleName, _ =>
        {
            using var stream = new MemoryStream(entryBytes, writable: false);
            return LoadFromStream(stream);
        });
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            return null;
        }

        // Delegate shared contracts and framework assemblies to the default load context.
        if (IsDelegatedToDefaultContext(simpleName))
        {
            return null;
        }

        if (_loadedBySimpleName.TryGetValue(simpleName, out var cached))
        {
            return cached;
        }

        var candidateFileName = simpleName + ".dll";
        if (_image.TryGetFileBytes(candidateFileName, out var bytes))
        {
            return _loadedBySimpleName.GetOrAdd(simpleName, _ =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return LoadFromStream(stream);
            });
        }

        // Also search subdirectories inside the verified image if a private dependency is nested
        foreach (var (relPath, fileBytes) in _image.Files)
        {
            if (relPath.EndsWith("/" + candidateFileName, StringComparison.OrdinalIgnoreCase))
            {
                return _loadedBySimpleName.GetOrAdd(simpleName, _ =>
                {
                    using var stream = new MemoryStream(fileBytes, writable: false);
                    return LoadFromStream(stream);
                });
            }
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName) ||
            unmanagedDllName.Contains("..", StringComparison.Ordinal) ||
            unmanagedDllName.Contains('/', StringComparison.Ordinal) ||
            unmanagedDllName.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(unmanagedDllName))
        {
            return IntPtr.Zero;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        var candidateRelativePaths = new[]
        {
            $"runtimes/{rid}/native/{unmanagedDllName}",
            $"runtimes/{rid}/native/{unmanagedDllName}.so",
            $"runtimes/{rid}/native/lib{unmanagedDllName}.so",
            $"runtimes/{rid}/native/{unmanagedDllName}.dll",
            $"runtimes/{rid}/native/{unmanagedDllName}.dylib",
        };

        foreach (var relPath in candidateRelativePaths)
        {
            if (_image.TryGetFileBytes(relPath, out var verifiedBytes))
            {
                var fullDiskPath = Path.Combine(
                    _image.ModuleDirectory,
                    relPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(fullDiskPath))
                {
                    throw new ModuleLoadException(_image.ModuleId, $"Verified native library '{relPath}' is missing on disk.");
                }

                var currentDiskBytes = File.ReadAllBytes(fullDiskPath);
                var expectedHash = IntegrityHashHelper.ComputeSha256HexLower(verifiedBytes);
                var actualHash = IntegrityHashHelper.ComputeSha256Bytes(currentDiskBytes);
                if (!IntegrityHashHelper.FixedTimeEqualsHex(expectedHash, actualHash))
                {
                    throw new ModuleLoadException(_image.ModuleId, $"Native library '{relPath}' failed integrity check prior to load.");
                }

                return LoadUnmanagedDllFromPath(fullDiskPath);
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsCollidingWithHost(string simpleName, HashSet<string> hostAssemblies)
    {
        if (hostAssemblies.Contains(simpleName))
        {
            return true;
        }

        if (string.Equals(simpleName, "Autheris.Api", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "Autheris.Application", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "Autheris.Domain", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "Autheris.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "Autheris.GraphQL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "Autheris.Modules.Abstractions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "FastSqlEngine", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "TrinoSqlEngine", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsDelegatedToDefaultContext(string simpleName)
    {
        if (simpleName.StartsWith("Autheris.Domain", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith("Autheris.Application", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith("Autheris.Modules.Abstractions", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "netstandard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(simpleName, "mscorlib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryReadManagedAssemblySimpleName(byte[] peBytes)
    {
        try
        {
            using var stream = new MemoryStream(peBytes, writable: false);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadataReader = peReader.GetMetadataReader();
            if (!metadataReader.IsAssembly)
            {
                return null;
            }

            var assemblyDef = metadataReader.GetAssemblyDefinition();
            return metadataReader.GetString(assemblyDef.Name);
        }
        catch
        {
            return null;
        }
    }
}
