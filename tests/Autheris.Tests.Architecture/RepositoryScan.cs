using System.Text.RegularExpressions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Shared, fail-closed repository scanning helpers for the ARCH-MNT ratchets. All paths are repo-relative with
/// forward slashes and all enumerations are sorted ordinally, so results are deterministic across platforms.
/// A missing repo root or an empty scan result throws (the calling test fails) instead of passing vacuously.
/// </summary>
internal static class RepositoryScan
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git"];
    private static readonly Lazy<string> Root = new(LocateRoot);
    private static readonly Lazy<IReadOnlyList<SourceFile>> SrcFiles = new(() => LoadCsFiles("src"));

    internal sealed record SourceFile(string RelativePath, string Text);

    public static string RepoRoot => Root.Value;

    /// <summary>All production C# files under src/, sorted by relative path. Throws when none are found.</summary>
    public static IReadOnlyList<SourceFile> ProductionFiles => SrcFiles.Value;

    /// <summary>Counts lines like <see cref="File.ReadLines(string)"/>: LF, CRLF and CR all end a line.</summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
            else if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
        }

        var last = text[^1];
        return last is '\n' or '\r' ? count : count + 1;
    }

    public static IReadOnlyList<string> EnumerateFiles(string relativeDirectory, string pattern)
    {
        var dir = Path.Combine(RepoRoot, relativeDirectory);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException("Directory not found: " + dir);
        }

        var files = new List<string>();
        Walk(dir, pattern, files);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"Scanner found no '{pattern}' files under {relativeDirectory}; refusing to pass vacuously.");
        }

        return files
            .Select(f => Normalize(Path.GetRelativePath(RepoRoot, f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public static string ReadText(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    /// <summary>
    /// Reads a baseline file (Baselines/*.txt): blank lines and lines starting with '#' are ignored.
    /// Throws when the file is missing so a deleted baseline cannot silently disable a ratchet.
    /// </summary>
    public static IReadOnlyList<string> ReadBaseline(string fileName)
    {
        var path = Path.Combine(RepoRoot, "tests", "Autheris.Tests.Architecture", "Baselines", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Baseline file is missing: " + path);
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#')
            .ToList();
    }

    /// <summary>Parses "key|integer" baseline lines. Duplicate keys throw.</summary>
    public static Dictionary<string, int> ParseCountBaseline(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var idx = line.LastIndexOf('|');
            if (idx <= 0 || !int.TryParse(line.AsSpan(idx + 1), out var value))
            {
                throw new FormatException("Malformed baseline line: " + line);
            }

            map.Add(line[..idx], value);
        }

        return map;
    }

    public static string Normalize(string path) => path.Replace('\\', '/');

    public static bool IsCommentLine(string trimmedLine) => trimmedLine.StartsWith("//", StringComparison.Ordinal);

    private static IReadOnlyList<SourceFile> LoadCsFiles(string relativeDirectory) =>
        EnumerateFiles(relativeDirectory, "*.cs")
            .Select(p => new SourceFile(p, ReadText(p)))
            .ToList();

    private static void Walk(string dir, string pattern, List<string> files)
    {
        files.AddRange(Directory.EnumerateFiles(dir, pattern));
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                Walk(sub, pattern, files);
            }
        }
    }

    private static string LocateRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repo root (Autheris.sln) from " + AppContext.BaseDirectory);
    }

    /// <summary>Trimmed, non-empty lines that do not start with "//" (block comments and trailing comments are not stripped).</summary>
    public static IEnumerable<string> CodeLines(string text) =>
        Regex.Split(text, "\r\n|\r|\n")
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !IsCommentLine(l));
}
