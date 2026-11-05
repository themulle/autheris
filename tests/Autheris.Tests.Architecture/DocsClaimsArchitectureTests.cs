using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-SIMPL-03 / WP-SIMPL-04: Enforces claims hygiene across documentation:
/// 1. Performance figures must link to a checked-in results or protocol file unless allowlisted.
/// 2. Deleted performance benchmarks document (performance-benchmarks.md) must not be referenced outside docs/plans.
/// 3. Protocol claims (JDBC, ODBC, gRPC, Flight SQL, Arrow Flight) must be backed by an interop test.
/// 4. Feature index rows in docs/features/README.md must point to existing files.
/// </summary>
public sealed class DocsClaimsArchitectureTests
{
    private static readonly Regex PerformanceRegex = new(
        @"(?i)req/s(ec)?|\bP(50|95|99)\b|\b\d+(\.\d+)?\s?ms\b",
        RegexOptions.Compiled);

    private static readonly Regex ResultsLinkRegex = new(
        @"\]\((?:\.\./)*benchmarks/(?:results|protocol)/[^\)]+\)",
        RegexOptions.Compiled);

    private static readonly Regex ProtocolRegex = new(
        @"\b(JDBC|ODBC|gRPC|Flight SQL|Arrow Flight)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FencedCodeBlockRegex = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly Regex BacktickSpanRegex = new(
        @"`[^`]*`",
        RegexOptions.Compiled);

    private static readonly Regex FeatureLinkRegex = new(
        @"\]\((f-[^)]+\.md)\)",
        RegexOptions.Compiled);

    [Fact]
    public void Performance_figures_link_a_results_file()
    {
        var files = GetMarkdownFiles();
        var allowlist = LoadClaimsAllowlist();
        var violations = new List<string>();

        foreach (var relativePath in files)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var cleanText = FencedCodeBlockRegex.Replace(text, string.Empty);
            var lines = cleanText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (PerformanceRegex.IsMatch(line) && !ResultsLinkRegex.IsMatch(line))
                {
                    var entryKey = $"{relativePath}:{line}";
                    if (!allowlist.Contains(entryKey))
                    {
                        violations.Add($"{relativePath}:{i + 1}: {line}");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Found {violations.Count} performance claims without links to benchmarks/results/ or protocol/:\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void No_reference_to_deleted_performance_benchmarks_doc()
    {
        var files = Directory.EnumerateFiles(RepositoryScan.RepoRoot, "*.md", SearchOption.AllDirectories)
            .Select(f => RepositoryScan.Normalize(Path.GetRelativePath(RepositoryScan.RepoRoot, f)))
            .Where(p => !p.StartsWith("docs/plans/", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("worktrees/", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains("/.git/") &&
                        !p.Contains("/bin/") &&
                        !p.Contains("/obj/") &&
                        !p.Contains("/node_modules/"))
            .ToList();

        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = RepositoryScan.ReadText(file);
            if (text.Contains("performance-benchmarks.md", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(file);
            }
        }

        violations.ShouldBeEmpty(
            "The following files still reference deleted performance-benchmarks.md:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void Protocol_claims_require_interop_test()
    {
        var files = GetMarkdownFiles()
            .Where(f => !f.StartsWith("docs/adr/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allowlist = LoadProtocolAllowlist();
        var violations = new List<string>();

        foreach (var relativePath in files)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var cleanText = FencedCodeBlockRegex.Replace(text, string.Empty);
            var lines = cleanText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var noCode = BacktickSpanRegex.Replace(line, string.Empty);
                var match = ProtocolRegex.Match(noCode);
                if (match.Success)
                {
                    var isAllowlisted = allowlist.Any(a =>
                        a.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase) &&
                        line.Contains(a.Substring, StringComparison.Ordinal));

                    if (!isAllowlisted)
                    {
                        violations.Add($"{relativePath}:{i + 1}: {match.Value} in '{line.Trim()}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Found {violations.Count} unverified protocol claims (JDBC/ODBC/gRPC/Flight SQL/Arrow Flight):\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void Feature_index_rows_point_to_existing_files()
    {
        var readmePath = "docs/features/README.md";
        var text = RepositoryScan.ReadText(readmePath);
        var dir = Path.Combine(RepositoryScan.RepoRoot, "docs", "features");

        var matches = FeatureLinkRegex.Matches(text);
        matches.Count.ShouldBeGreaterThan(0, "docs/features/README.md should contain feature links.");

        var missing = new List<string>();
        foreach (Match match in matches)
        {
            var linkTarget = match.Groups[1].Value;
            var fullPath = Path.Combine(dir, linkTarget);
            if (!File.Exists(fullPath))
            {
                missing.Add(linkTarget);
            }
        }

        missing.ShouldBeEmpty(
            $"The following feature links in docs/features/README.md do not exist:\n" +
            string.Join("\n", missing));
    }

    private static IReadOnlyList<string> GetMarkdownFiles()
    {
        var result = new List<string>();
        var root = RepositoryScan.RepoRoot;

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            result.Add(RepositoryScan.Normalize(Path.GetRelativePath(root, file)));
        }

        var docsDir = Path.Combine(root, "docs");
        if (Directory.Exists(docsDir))
        {
            foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
            {
                var rel = RepositoryScan.Normalize(Path.GetRelativePath(root, file));
                if (!rel.StartsWith("docs/plans/", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(rel);
                }
            }
        }

        return result.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> LoadClaimsAllowlist()
    {
        try
        {
            return new HashSet<string>(RepositoryScan.ReadBaseline("docs-claims-allowlist.txt"), StringComparer.Ordinal);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    private sealed record ProtocolAllowlistEntry(string Path, string Substring, string TestMethod);

    private static List<ProtocolAllowlistEntry> LoadProtocolAllowlist()
    {
        var result = new List<ProtocolAllowlistEntry>();
        try
        {
            var lines = RepositoryScan.ReadBaseline("protocol-claim-allowlist.txt");
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    result.Add(new ProtocolAllowlistEntry(parts[0].Trim(), parts[1].Trim(), parts[2].Trim()));
                }
            }
        }
        catch (FileNotFoundException)
        {
        }

        return result;
    }
}
