using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-SIMPL-05: Enforces documentation hygiene and integrity:
/// 1. No absolute file:// links across any markdown files.
/// 2. Relative markdown links in gated documentation resolve.
/// 3. Backticked source paths point to existing files (SEC-SIMPL-74).
/// 4. No Status tags or "100% GA" / "production-ready" marketing claims outside docs/plans.
/// 5. Docs are written in English (German stop-word density gate).
/// </summary>
public sealed class DocsIntegrityTests
{
    private static readonly Regex FencedCodeBlockRegex = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly Regex LinkRegex = new(
        @"\[([^\]]*)\]\(([^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex BacktickedSourcePathRegex = new(
        @"`((?:src|tests|docs|deploy|benchmarks|scripts)/[\w./-]+)`",
        RegexOptions.Compiled);

    private static readonly Regex StatusTagRegex = new(
        @"^\s*(\*\*)?Status:?(\*\*)?:?\s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarketingClaimsRegex = new(
        @"100%\s*GA|production-ready",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GermanWordsRegex = new(
        @"\b[\wüäöß]+\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> GermanStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "nicht", "mit", "für", "ist", "werden", "eine"
    };

    [Fact]
    public void No_absolute_file_links()
    {
        var allMarkdownFiles = GetAllMarkdownFiles();
        var violations = new List<string>();

        foreach (var relativePath in allMarkdownFiles)
        {
            var text = RepositoryScan.ReadText(relativePath);
            if (text.Contains("file://", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(relativePath);
            }
        }

        violations.ShouldBeEmpty(
            $"Found {violations.Count} files with absolute file:// links:\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void Relative_links_resolve()
    {
        var gatedFiles = GetGatedMarkdownFiles();
        var brokenLinks = new List<string>();

        foreach (var relativePath in gatedFiles)
        {
            var fullPath = Path.Combine(RepositoryScan.RepoRoot, relativePath);
            var fileDir = Path.GetDirectoryName(fullPath)!;
            var text = RepositoryScan.ReadText(relativePath);
            var cleanText = FencedCodeBlockRegex.Replace(text, string.Empty);

            var matches = LinkRegex.Matches(cleanText);
            foreach (Match match in matches)
            {
                var target = match.Groups[2].Value.Trim();
                if (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith('#'))
                {
                    continue;
                }

                var cleanTarget = target.Split('#')[0].Split('?')[0];
                if (string.IsNullOrWhiteSpace(cleanTarget))
                {
                    continue;
                }

                cleanTarget = WebUtility.UrlDecode(cleanTarget);
                var resolvedPath = Path.GetFullPath(Path.Combine(fileDir, cleanTarget));
                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    brokenLinks.Add($"{relativePath}: [{match.Groups[1].Value}]({target}) -> {cleanTarget}");
                }
            }
        }

        brokenLinks.ShouldBeEmpty(
            $"Found {brokenLinks.Count} broken relative links in gated docs:\n" +
            string.Join("\n", brokenLinks.Take(50)));
    }

    [Fact]
    public void Backticked_source_paths_exist()
    {
        var gatedFiles = GetGatedMarkdownFiles()
            .Where(f => !f.StartsWith("docs/adr/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missing = new List<string>();

        foreach (var relativePath in gatedFiles)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var cleanText = FencedCodeBlockRegex.Replace(text, string.Empty);

            var matches = BacktickedSourcePathRegex.Matches(cleanText);
            foreach (Match match in matches)
            {
                var token = match.Groups[1].Value;
                if (token.Contains('*') || token.Contains('<') || token.Contains('{'))
                {
                    continue;
                }

                var fullPath = Path.Combine(RepositoryScan.RepoRoot, token);
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    missing.Add($"{relativePath}: `{token}`");
                }
            }
        }

        missing.ShouldBeEmpty(
            $"Found {missing.Count} referenced source paths in backticks that do not exist:\n" +
            string.Join("\n", missing.Take(50)));
    }

    [Fact]
    public void No_status_tags_outside_plans()
    {
        var gatedFiles = GetGatedMarkdownFiles()
            .Where(f => !f.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();

        foreach (var relativePath in gatedFiles)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var cleanText = FencedCodeBlockRegex.Replace(text, string.Empty);
            var lines = cleanText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (StatusTagRegex.IsMatch(line) || MarketingClaimsRegex.IsMatch(line))
                {
                    violations.Add($"{relativePath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Found {violations.Count} status tags or GA/production-ready claims in gated docs:\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void Docs_are_english()
    {
        var gatedFiles = GetGatedMarkdownFiles()
            .Where(f => !f.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase) &&
                        !f.StartsWith("docs/adr/", StringComparison.OrdinalIgnoreCase) &&
                        !f.StartsWith("docs/prd/", StringComparison.OrdinalIgnoreCase) &&
                        !f.StartsWith("docs/extensions/", StringComparison.OrdinalIgnoreCase) &&
                        !f.Equals("docs/security/dep-12-git-history-cleanup.md", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();

        foreach (var relativePath in gatedFiles)
        {
            var text = RepositoryScan.ReadText(relativePath);
            var matches = GermanWordsRegex.Matches(text);
            if (matches.Count < 50)
            {
                continue;
            }

            var stopWordCount = matches.Count(m => GermanStopWords.Contains(m.Value));
            var density = (double)stopWordCount / matches.Count;

            if (density >= 0.03)
            {
                violations.Add($"{relativePath}: stop-word density {density:P2} ({stopWordCount}/{matches.Count})");
            }
        }

        violations.ShouldBeEmpty(
            $"Found {violations.Count} documents exceeding German stop-word threshold:\n" +
            string.Join("\n", violations.Take(50)));
    }

    private static IReadOnlyList<string> GetAllMarkdownFiles() =>
        RepositoryScan.EnumerateFiles("", "*.md");

    private static IReadOnlyList<string> GetGatedMarkdownFiles()
    {
        var root = RepositoryScan.RepoRoot;
        var all = GetAllMarkdownFiles();

        return all.Where(rel =>
        {
            if (rel.StartsWith("docs/plans/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (rel.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                (!rel.Contains('/') && rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ||
                rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
                (rel.StartsWith("src/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)) ||
                (rel.StartsWith("modules/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }).ToList();
    }
}
