using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;

namespace Autheris.Infrastructure.Plugins;

public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly PluginTrustList? _trustList;

    /// <param name="pluginPath">Path of the main plugin assembly (used for dependency resolution).</param>
    /// <param name="trustList">SEC M-27: when set, every managed dependency and native library resolved from the
    /// plugin directory must match its configured SHA-256 hash; unlisted files are rejected.</param>
    public PluginAssemblyLoadContext(string pluginPath, PluginTrustList? trustList = null) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _trustList = trustList;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared host contracts and runtime assemblies must resolve via Default ALC
        if (IsSharedAssembly(assemblyName.Name))
        {
            return null; // delegates to AssemblyLoadContext.Default
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            if (_trustList == null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // SEC M-27: hash-checked load from the verified bytes.
            var bytes = _trustList.ReadVerifiedBytes(assemblyPath);
            using var stream = new MemoryStream(bytes, writable: false);
            return LoadFromStream(stream);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            if (_trustList != null)
            {
                // SEC M-27: native libraries must be on the trust list (residual TOCTOU: native code can only be
                // loaded by path; keep the plugin directory read-only for the gateway process).
                if (!_trustList.TryGetExpectedHash(libraryPath, out _))
                {
                    throw new SecurityException($"Sicherheitsfehler: Native Bibliothek '{Path.GetFileName(libraryPath)}' ist nicht in Plugins:TrustedPluginHashes verzeichnet.");
                }

                _trustList.VerifyFile(libraryPath);
            }

            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

    private static bool IsSharedAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith("Autheris.Domain", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Autheris.Application", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
    }
}
