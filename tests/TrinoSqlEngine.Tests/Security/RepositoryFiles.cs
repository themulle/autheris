namespace TrinoSqlEngine.Tests.Security;

/// <summary>Finds files of the repository (the gate reads sources to prove traceability and to detect controls of other packages).</summary>
internal static class RepositoryFiles
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    public static string Root => RootDirectory.Value;

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Autheris.sln was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Files below the repository root that match <paramref name="pattern"/> (<c>dir/**/*.cs</c> or a single path), excluding build output.</summary>
    public static IEnumerable<string> Search(string pattern)
    {
        int star = pattern.IndexOf("**", StringComparison.Ordinal);
        if (star < 0)
        {
            string single = Path.Combine(Root, pattern);
            return File.Exists(single) ? new[] { single } : Array.Empty<string>();
        }

        string directory = Path.Combine(Root, pattern[..star].TrimEnd('/'));
        string filePattern = pattern[(star + 3)..];
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, filePattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
