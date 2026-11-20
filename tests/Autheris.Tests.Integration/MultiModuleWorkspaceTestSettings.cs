namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// Stages multiple workspace modules under a clean temporary directory so integration hosts that exercise
/// endpoints across multiple extracted modules (for example <c>dbt</c> and <c>events</c>) can load them
/// together without colliding on single-project <c>bin/</c> paths or scanning <c>obj/</c>.
/// </summary>
internal static class MultiModuleWorkspaceTestSettings
{
    private static readonly object StagingLock = new();

    public static void Apply(Action<string, string> useSetting, params string[] moduleIds)
    {
        ArgumentNullException.ThrowIfNull(useSetting);
        if (moduleIds.Length == 0)
        {
            throw new ArgumentException("At least one module id is required.", nameof(moduleIds));
        }

        var root = FindRepositoryRoot();
        var configuration = DetectBuildConfiguration();
        var targetFramework = DetectTargetFramework();
        var key = $"{configuration}-{targetFramework}-{string.Join("-", moduleIds)}";
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"autheris-test-ws-{Environment.ProcessId}", key);

        lock (StagingLock)
        {
            for (int i = 0; i < moduleIds.Length; i++)
            {
                var moduleId = moduleIds[i];
                var pascal = ToPascalModuleName(moduleId);
                var sourceBinDir = FindModuleBinDirectory(root, pascal, configuration, targetFramework);
                var destDir = Path.Combine(workspaceRoot, pascal);
                Directory.CreateDirectory(destDir);

                foreach (var dllPath in Directory.EnumerateFiles(sourceBinDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var destPath = Path.Combine(destDir, Path.GetFileName(dllPath));
                    if (!File.Exists(destPath))
                    {
                        File.Copy(dllPath, destPath, overwrite: true);
                    }
                }
            }
        }

        for (int i = 0; i < moduleIds.Length; i++)
        {
            useSetting($"Gateway:Modules:Enabled:{i}", moduleIds[i]);
        }

        useSetting("Gateway:Modules:AllowUnsignedInDevelopment", "true");
        useSetting("Gateway:Modules:WorkspacePath", workspaceRoot);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string DetectTargetFramework() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private static string DetectBuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name ?? "Release";

    private static string ToPascalModuleName(string moduleId) =>
        string.Concat(moduleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string FindModuleBinDirectory(string repoRoot, string pascal, string configuration, string targetFramework)
    {
        var binRoot = Path.Combine(repoRoot, "modules", $"Autheris.Modules.{pascal}", "bin");
        var preferred = Path.Combine(binRoot, configuration, targetFramework);
        var assemblyName = $"Autheris.Modules.{pascal}.dll";
        if (File.Exists(Path.Combine(preferred, assemblyName)))
        {
            return preferred;
        }

        var found = Directory.EnumerateFiles(binRoot, assemblyName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not locate '{assemblyName}' under '{binRoot}'.");
        return Path.GetDirectoryName(found)!;
    }
}
