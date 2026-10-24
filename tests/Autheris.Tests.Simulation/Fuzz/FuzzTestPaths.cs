namespace Autheris.Tests.Simulation.Fuzz;

/// <summary>Repository paths of the parser fuzz tests (REQ-SIMTEST P3).</summary>
internal static class FuzzTestPaths
{
    public static string RepoRoot { get; } = FindRoot();

    public static string FuzzProject => Path.Combine(RepoRoot, "tests", "Autheris.Tests.Fuzz");

    public static string Corpus => Path.Combine(FuzzProject, "corpus");

    public static string Findings => Path.Combine(FuzzProject, "findings");

    public static string Dql => Path.Combine(RepoRoot, "tests", "Autheris.Tests.SqlCompilerGate", "Corpus", "dql.sql");

    public static string Script => Path.Combine(RepoRoot, "scripts", "sim", "run-parser-fuzz.sh");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tests", "Autheris.Tests.Fuzz", "Autheris.Tests.Fuzz.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris-fuzz-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
