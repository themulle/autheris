namespace Autheris.Api.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
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
/// <remarks>
/// The context is deliberately non-collectible: modules register services, routes and background
/// work into the host for the life of the process and are never unloaded, so collectibility would only
/// add leak-prone teardown paths without a supported unload scenario.
/// </remarks>
public sealed class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly string[] ReservedHostAssemblyNames =
    [
        "Autheris.Api",
        "Autheris.Application",
        "Autheris.Domain",
        "Autheris.Infrastructure",
        "Autheris.GraphQL",
        "Autheris.Modules.Abstractions",
        "FastSqlEngine",
        "TrinoSqlEngine",
        "netstandard",
        "mscorlib",
    ];

    private static readonly Lazy<string> ProcessNativeRoot = new(CreateDefaultNativeRoot);

    private readonly VerifiedModuleImage _image;
    private readonly ConcurrentDictionary<string, Assembly> _loadedBySimpleName = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrozenSet<string> _delegatedHostAssemblies;
    private readonly string? _nativeRootOverride;
    private readonly Dictionary<string, string> _nativeCopies = new(StringComparer.Ordinal);
    private readonly object _nativeLock = new();
    private string? _nativeContextDirectory;

    public ModuleAssemblyLoadContext(VerifiedModuleImage image, string? nativeRootOverride = null)
        : base(name: $"AutherisModule:{image?.ModuleId ?? "unknown"}", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _nativeRootOverride = nativeRootOverride;

        var delegated = new HashSet<string>(ReservedHostAssemblyNames, StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in Default.Assemblies)
        {
            var name = loaded.GetName().Name;
            if (!string.IsNullOrEmpty(name))
            {
                delegated.Add(name);
            }
        }

        _delegatedHostAssemblies = delegated.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
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

        var seenSimpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (!seenSimpleNames.Add(fileSimpleName))
            {
                throw new ModuleLoadException(
                    _image.ModuleId,
                    $"Module image contains duplicate assembly simple name '{fileSimpleName}' (file '{relativePath}').");
            }

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
        if (IsDelegatedToHost(simpleName))
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
                var copyPath = MaterializeVerifiedNative(relPath, verifiedBytes);
                EnsureNativeCopyIntact(copyPath, relPath, verifiedBytes);
                return LoadUnmanagedDllFromPath(copyPath);
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

    /// <summary>
    /// True only for exact simple-name matches of host assemblies (reserved contract assemblies and everything
    /// loaded into the default context when this context was created); similar prefixes are not delegated.
    /// </summary>
    internal bool IsDelegatedToHost(string simpleName) => _delegatedHostAssemblies.Contains(simpleName);

    /// <summary>
    /// Writes the already verified native library bytes to a private, read-only copy and returns its path.
    /// The copy lives in a per-context directory below the per-process private native root.
    /// </summary>
    internal string MaterializeVerifiedNative(string relativePath, ReadOnlySpan<byte> verifiedBytes)
    {
        lock (_nativeLock)
        {
            if (_nativeCopies.TryGetValue(relativePath, out var existing))
            {
                return existing;
            }

            try
            {
                var root = _nativeRootOverride ?? ProcessNativeRoot.Value;
                EnsurePrivateDirectory(root);

                _nativeContextDirectory ??= Path.Combine(root, Guid.NewGuid().ToString("N"));
                EnsurePrivateDirectory(_nativeContextDirectory);

                var copyPath = Path.Combine(_nativeContextDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                EnsurePrivateDirectory(Path.GetDirectoryName(copyPath)!);
                using (var stream = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(verifiedBytes);
                }

                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(copyPath, FileAttributes.ReadOnly);
                }
                else
                {
                    File.SetUnixFileMode(copyPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }

                _nativeCopies[relativePath] = copyPath;
                return copyPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModuleLoadException(_image.ModuleId, $"Native library '{relativePath}' could not be staged: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Re-hashes the private native copy and throws when it no longer matches the verified bytes.
    /// </summary>
    internal void EnsureNativeCopyIntact(string copyPath, string relativePath, ReadOnlySpan<byte> verifiedBytes)
    {
        byte[] currentBytes;
        try
        {
            currentBytes = File.ReadAllBytes(copyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModuleLoadException(_image.ModuleId, $"Native library '{relativePath}' failed integrity check prior to load.", ex);
        }

        var expectedHash = IntegrityHashHelper.ComputeSha256HexLower(verifiedBytes);
        var actualHash = IntegrityHashHelper.ComputeSha256Bytes(currentBytes);
        if (!IntegrityHashHelper.FixedTimeEqualsHex(expectedHash, actualHash))
        {
            throw new ModuleLoadException(_image.ModuleId, $"Native library '{relativePath}' failed integrity check prior to load.");
        }
    }

    private static string CreateDefaultNativeRoot()
    {
        var parent = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autheris", "native")
            : Path.GetTempPath();
        var root = Path.Combine(parent, $"autheris-native-{Environment.ProcessId}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        };
        return root;
    }

    private void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, PrivateMode);

        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new ModuleLoadException(_image.ModuleId, $"Native library root '{path}' must not be a symbolic link.");
        }

        if (File.GetUnixFileMode(path) != PrivateMode)
        {
            throw new ModuleLoadException(
                _image.ModuleId,
                $"Native library root '{path}' must have mode 0700 and be owned by the process user.");
        }

        var owner = UnixFileStat.TryGetOwnerUid(path);
        var self = UnixFileStat.TryGetEffectiveUid();
        if (owner is not { } o || self is not { } e || o != e)
        {
            throw new ModuleLoadException(
                _image.ModuleId,
                $"Native library root '{path}' must have mode 0700 and be owned by the process user.");
        }
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
