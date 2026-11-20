namespace Autheris.Tests.Integration;

/// <summary>
/// Test hosts load the dbt module from the in-repo build output through the Development-only
/// <c>Gateway:Modules:WorkspacePath</c> (no implicit workspace fallback) and enable it explicitly.
/// </summary>
internal static class DbtWorkspaceTestSettings
{
    public static void Apply(Action<string, string> useSetting, int enabledIndex = 0)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        useSetting($"Gateway:Modules:Enabled:{enabledIndex}", "dbt");
        useSetting("Gateway:Modules:AllowUnsignedInDevelopment", "true");
        useSetting("Gateway:Modules:WorkspacePath", Path.Combine(root, "modules", "Autheris.Modules.Dbt", "bin"));
    }
}
