using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #286 (SEC-286-1): the governance runtime role holds no DDL right, so Infrastructure persistence and observability code never
/// creates, alters or drops schema objects. Schema belongs exclusively to the baseline migrations (<c>Persistence/Migrations/</c>).
/// </summary>
[Trait("Category", "Security")]
public sealed class NoRuntimeGovernanceDdlArchitectureTests
{
    private static readonly Regex Ddl = new(
        @"\b(CREATE\s+(OR\s+(REPLACE|ALTER)\s+)?(UNIQUE\s+)?(CLUSTERED\s+|NONCLUSTERED\s+)?(TABLE|INDEX|TRIGGER|VIEW|SEQUENCE|FUNCTION|PROCEDURE)"
        + @"|ALTER\s+(TABLE|INDEX|VIEW|TRIGGER)|DROP\s+(TABLE|INDEX|VIEW|TRIGGER|SEQUENCE)|TRUNCATE\s+TABLE"
        + @"|SELECT\s[^;""]*?\sINTO\s+(#|\[|dbo\.|public\.)[\w\.\[\]#]*\s+FROM)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Exact relative path -> exact number of expected hits. Empty: no runtime DDL is allowed. A new hit in a listed file still fails.
    private static readonly Dictionary<string, int> Allowlist = new(StringComparer.Ordinal);

    private static readonly string[] ScannedFolders =
    [
        "src/Autheris.Infrastructure/Persistence",
        "src/Autheris.Infrastructure/Observability"
    ];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"(?m)^\s*//.*$", string.Empty);
    }

    private static IEnumerable<(string Relative, string Text)> SourceFiles(string folder)
    {
        string root = RepositoryRoot();
        string sep = Path.DirectorySeparatorChar.ToString();
        return Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"))
            .Select(p => (Relative: Path.GetRelativePath(root, p).Replace('\\', '/'), Path: p))
            .Select(x => (x.Relative, StripComments(File.ReadAllText(x.Path))));
    }

    private static Dictionary<string, int> Hits(string text, string relative) =>
        relative.Contains("/Persistence/Migrations/", StringComparison.Ordinal) || Ddl.Count(text) == 0
            ? []
            : new Dictionary<string, int> { [relative] = Ddl.Count(text) };

    [Fact]
    public void AllPersistenceAndObservability_ContainNoDdl_ExceptAllowlistedExactPaths()
    {
        var offenders = new List<string>();
        foreach (var folder in ScannedFolders)
        {
            foreach (var (relative, text) in SourceFiles(folder))
            {
                foreach (var (path, count) in Hits(text, relative))
                {
                    if (!Allowlist.TryGetValue(path, out int expected) || expected != count)
                    {
                        offenders.Add($"{path} ({count} DDL statement(s))");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty("Runtime code must not run DDL; add the object to the baseline migrations instead: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DdlScan_DetectsKnownBaselineDdl_PositiveControl()
    {
        var baseline = SourceFiles("src/Autheris.Infrastructure/Persistence/Migrations")
            .Single(f => f.Relative.EndsWith("/SqliteBaselineSchema.cs", StringComparison.Ordinal));

        Ddl.IsMatch(baseline.Text).ShouldBeTrue();
        Ddl.IsMatch("IF OBJECT_ID(N'dbo.X', N'U') IS NULL CREATE TABLE dbo.X (a INT)").ShouldBeTrue();
        Ddl.IsMatch("DROP TABLE X").ShouldBeTrue();
        Ddl.IsMatch("SELECT a INTO #t FROM b").ShouldBeTrue();
        Ddl.IsMatch("INSERT INTO CONSUMER_APP_DAILY (a) VALUES (@a)").ShouldBeFalse();
    }

    [Fact]
    public void EnsureTablesCreatedAsync_DoesNotExist()
    {
        var offenders = SourceFiles("src/Autheris.Infrastructure")
            .Where(f => f.Text.Contains("EnsureTablesCreatedAsync", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .ToList();

        offenders.ShouldBeEmpty("Lazy runtime table creation is forbidden (Issue #286): " + string.Join(", ", offenders));
    }
}
