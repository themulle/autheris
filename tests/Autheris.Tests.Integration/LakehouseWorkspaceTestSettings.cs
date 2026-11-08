namespace Autheris.Tests.Integration;

/// <summary>
/// Test hosts load the lakehouse module from the in-repo build output through the Development-only
/// <c>Gateway:Modules:WorkspacePath</c> (SEC-50: no implicit workspace fallback).
/// </summary>
internal static class LakehouseWorkspaceTestSettings
{
    public static void Apply(Action<string, string> useSetting)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        useSetting("Gateway:Modules:AllowUnsignedInDevelopment", "true");
        useSetting("Gateway:Modules:WorkspacePath", Path.Combine(root, "modules", "Autheris.Modules.Lakehouse", "bin"));
    }
}
