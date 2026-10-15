using System.Collections.Concurrent;
using System.Reflection;
using Mono.Cecil;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-TS-04: caches read-only <see cref="ModuleDefinition"/> instances across architecture tests so each assembly is read from disk once per test process.
/// Callers must not dispose returned modules.
/// </summary>
internal static class CecilModuleCache
{
    private static readonly ConcurrentDictionary<string, ModuleDefinition> Modules = new(StringComparer.Ordinal);

    public static ModuleDefinition For(Assembly assembly) => For(assembly.Location);

    public static ModuleDefinition For(string assemblyPath) =>
        Modules.GetOrAdd(Path.GetFullPath(assemblyPath), static path => ModuleDefinition.ReadModule(path));
}
