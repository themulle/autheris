namespace Autheris.Tests.Integration;

using System;
using System.IO;

/// <summary>
/// Test-host helper that configures <c>Gateway:Modules:WorkspacePath</c> and enables the <c>openmetadata</c> module.
/// </summary>
internal static class OpenMetadataWorkspaceTestSettings
{
    public static void Apply(Action<string, string> useSetting, int enabledIndex = 0)
    {
        var root = FindRepositoryRoot();
        useSetting($"Gateway:Modules:Enabled:{enabledIndex}", "openmetadata");
        useSetting("Gateway:Modules:AllowUnsignedInDevelopment", "true");
        useSetting("Gateway:Modules:WorkspacePath", Path.Combine(root, "modules", "Autheris.Modules.OpenMetadata", "bin"));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (Autheris.sln) not found.");
    }
}
