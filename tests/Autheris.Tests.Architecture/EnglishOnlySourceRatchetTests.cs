using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Enforces the repository English-only rule across production code, modules, CI workflows, and deploy configs
/// so German comments, labels, and user-facing strings cannot regress.
/// </summary>
public sealed partial class EnglishOnlySourceRatchetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] ScannedDirectories = ["src", "modules", ".github", "deploy"];
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".claude", "worktrees"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".g4", ".sql", ".md",
        ".yml", ".yaml", ".py", ".sh", ".ps1", ".js", ".conf", ".tpl", ".env", ".example"
    };

    // Documented shrink-only exceptions: third-party vendored UI, bilingual search dictionaries/fold cases,
    // and legacy validation/options strings locked by snapshot tests (startup-validator-failures.txt / configuration.md).
    private static readonly IReadOnlyDictionary<string, int> AllowlistedCodeFiles = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Autheris.Api/Extensions/DependencyInjection/StartupChecks/AuthenticationStartupChecks.cs"] = 1,
        ["src/Autheris.Api/Extensions/DependencyInjection/StartupChecks/PolicyAndNetworkStartupChecks.cs"] = 1,
        ["src/Autheris.Api/Extensions/DependencyInjection/StartupChecks/ProductionStartupChecks.cs"] = 2,
        ["src/Autheris.Application/Catalog/Search/DiacriticNormalizer.cs"] = 7,
        ["src/Autheris.Application/Catalog/Search/SmartSchemaTokenizer.cs"] = 1,
        ["src/Autheris.Application/Mcp/Pruning/SemanticToolPruner.cs"] = 1,
        ["src/Autheris.Domain/Options/ClassificationOptions.cs"] = 10
    };

    // DiscoveryTextSanitizer quotes "z. B." inside an English doc comment describing sentence-boundary handling (#799).
    private static readonly IReadOnlyDictionary<string, int> AllowlistedCommentFiles = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Autheris.Application/Catalog/Discovery/DiscoveryTextSanitizer.cs"] = 1
    };

    [GeneratedRegex(@"[\u00C4\u00D6\u00DC\u00E4\u00F6\u00FC\u00DF]")]
    private static partial Regex GermanDiacriticRegex();

    [GeneratedRegex(@"\bz\.\s*B\b")]
    private static partial Regex GermanAbbreviationRegex();

    [GeneratedRegex(@"\b(?:NIEMALS|PRODUKTIV|EINSETZEN|AUSSCHLIESSLICH|Sicherheitsverletzung|nicht|keine|keinen|keiner|keines|wird|werden|wurde|wurden|fuer|ueber|oeffentlich|oeffentliches|Beispiel|Hinweis|Achtung|Verwendung|Entwicklung|Umgebung|Pflicht|Fehler|Zugriff|Tabellen|Spalten|Abfrage|Abfragen|Benutzer|Freigabe|Maskierung|Pruefung|Anwendung|Standardmaessig|anwendbar|bereits|sofern|solange|damit|jedoch|sondern|zwischen|berechnet|liefert|erstellt|prueft|erzeugt|laedt|speichert|zwingend|bleibt|gesperrt|verboten|beachten|nutzbar|ausgeliefert|eingesetzt|dargestellt|abgebrochen|beendet|aktualisiert|vorhanden|ausschliesslich|muessen|koennen|duerfen|waehrend|und|oder|einer|einen|einem|eines|sind|zum|zur|auch|noch|jeder|welche|welcher|welches|sich|durch|gegen|ohne)\b")]
    private static partial Regex StrongGermanWordRegex();

    [GeneratedRegex(@"\b(?:der|die|das|mit|auf|aus|bei|nach|vor|von|vom|als|nur|wenn|ist|war|waren|hat|haben|kann|muss|soll|sollen|darf|alle|eine|mehr|sehr|jede|jedes|dieser|diese|dieses|unter)\b")]
    private static partial Regex GermanStopWordRegex();

    [Fact]
    public void Source_Workflows_And_DeployConfigs_Are_EnglishOnly()
    {
        var files = CollectTargetFiles().ToList();
        files.Count.ShouldBeGreaterThan(500, "Scanner must inspect src/, modules/, .github/, deploy/, and root container files.");
        files.ShouldContain(f => f.Path == "docker-compose.yml");
        files.ShouldContain(f => f.Path == ".github/workflows/release.yml");
        files.ShouldContain(f => f.Path == "deploy/podman-compose.yaml");

        var (problems, hints) = Evaluate(files, AllowlistedCodeFiles, AllowlistedCommentFiles);

        foreach (var hint in hints)
        {
            output.WriteLine("HINT (not a failure): " + hint);
        }

        problems.ShouldBeEmpty("Code, workflows, and deploy configs must be English-only:\n" + string.Join(Environment.NewLine, problems.Take(50)));
    }

    [Fact]
    public void Scanner_DetectsPlantedGermanInCodeWorkflowAndCompose()
    {
        var germanComment = "// Pr" + "\u00FC" + "fung f" + "\u00FC" + "r Zugriff";
        var germanString = "throw new InvalidOperationException(\"nicht " + "anwendbar\");";
        var germanWorkflow = "description: 'Release Tag (z." + " B. v1.0.0)'";
        var germanCompose = "# !!! NIEMALS " + "PRODUKTIV EINSETZEN !!!";

        var sampleFiles = new (string Path, string Text)[]
        {
            ("src/Sample/File.cs", germanComment + "\n" + germanString),
            (".github/workflows/sample.yml", germanWorkflow),
            ("docker-compose.yml", germanCompose),
            ("src/Sample/English.cs", "// Validate caller permissions before running query.\nreturn true;")
        };

        var (problems, _) = Evaluate(sampleFiles, new Dictionary<string, int>(), new Dictionary<string, int>());
        problems.ShouldContain(p => p.StartsWith("COMMENT src/Sample/File.cs:1", StringComparison.Ordinal));
        problems.ShouldContain(p => p.StartsWith("CODE src/Sample/File.cs:2", StringComparison.Ordinal));
        problems.ShouldContain(p => p.StartsWith("CONFIG .github/workflows/sample.yml:1", StringComparison.Ordinal));
        problems.ShouldContain(p => p.StartsWith("CONFIG docker-compose.yml:1", StringComparison.Ordinal));
        problems.ShouldNotContain(p => p.Contains("English.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_EnforcesShrinkOnlyAllowlistCeilings()
    {
        var allow = new Dictionary<string, int>(StringComparer.Ordinal) { ["src/Sample/Legacy.cs"] = 1 };
        var ok = new[] { ("src/Sample/Legacy.cs", "var x = \"nicht " + "anwendbar\";") };
        var grown = new[] { ("src/Sample/Legacy.cs", "var x = \"nicht " + "anwendbar\";\nvar y = \"keine " + "Freigabe\";") };
        var commentInAllowlisted = new[] { ("src/Sample/Legacy.cs", "// nicht " + "anwendbar") };

        Evaluate(ok, allow, new Dictionary<string, int>()).Problems.ShouldBeEmpty();
        Evaluate(grown, allow, new Dictionary<string, int>()).Problems.ShouldContain(p => p.StartsWith("GROWN src/Sample/Legacy.cs", StringComparison.Ordinal));
        Evaluate(commentInAllowlisted, allow, new Dictionary<string, int>()).Problems.ShouldContain(p => p.StartsWith("COMMENT src/Sample/Legacy.cs", StringComparison.Ordinal));
    }

    internal static bool IsGermanLine(string line) =>
        GermanDiacriticRegex().IsMatch(line)
        || GermanAbbreviationRegex().IsMatch(line)
        || StrongGermanWordRegex().IsMatch(line)
        || GermanStopWordRegex().Count(line) >= 2;

    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    private static (List<string> Problems, List<string> Hints) Evaluate(
        IEnumerable<(string Path, string Text)> files,
        IReadOnlyDictionary<string, int> codeAllowlist,
        IReadOnlyDictionary<string, int> commentAllowlist)
    {
        var problems = new List<string>();
        var hints = new List<string>();
        var seenCode = new HashSet<string>(StringComparer.Ordinal);
        var seenComments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, text) in files)
        {
            var isCSharp = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var commentHits = new List<int>();
            var codeHits = new List<int>();

            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsGermanLine(lines[i]))
                {
                    continue;
                }

                if (isCSharp && IsCommentLine(lines[i]))
                {
                    commentHits.Add(i + 1);
                }
                else
                {
                    codeHits.Add(i + 1);
                }
            }

            if (commentHits.Count > 0)
            {
                seenComments.Add(path);
                if (!commentAllowlist.TryGetValue(path, out var maxComments))
                {
                    problems.Add($"COMMENT {path}:{commentHits[0]} ({commentHits.Count} line(s))");
                }
                else if (commentHits.Count > maxComments)
                {
                    problems.Add($"GROWN COMMENT {path}: {commentHits.Count} > ceiling {maxComments}");
                }
                else if (commentHits.Count < maxComments)
                {
                    hints.Add($"{path}: comment German lines dropped to {commentHits.Count} (< {maxComments}); lower ceiling.");
                }
            }

            if (codeHits.Count > 0)
            {
                seenCode.Add(path);
                if (!isCSharp)
                {
                    problems.Add($"CONFIG {path}:{codeHits[0]} ({codeHits.Count} line(s))");
                }
                else if (!codeAllowlist.TryGetValue(path, out var maxCode))
                {
                    problems.Add($"CODE {path}:{codeHits[0]} ({codeHits.Count} line(s))");
                }
                else if (codeHits.Count > maxCode)
                {
                    problems.Add($"GROWN {path}: {codeHits.Count} > ceiling {maxCode}");
                }
                else if (codeHits.Count < maxCode)
                {
                    hints.Add($"{path}: code German lines dropped to {codeHits.Count} (< {maxCode}); lower ceiling.");
                }
            }
        }

        foreach (var path in codeAllowlist.Keys.Where(p => !seenCode.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            hints.Add($"{path}: no longer has German lines; remove from AllowlistedCodeFiles.");
        }

        foreach (var path in commentAllowlist.Keys.Where(p => !seenComments.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            hints.Add($"{path}: no longer has allowlisted comments; remove from AllowlistedCommentFiles.");
        }

        return (problems, hints);
    }

    private static IEnumerable<(string Path, string Text)> CollectTargetFiles()
    {
        var root = RepositoryScan.RepoRoot;
        var relativePaths = new List<string>();

        foreach (var dir in ScannedDirectories)
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir))
            {
                throw new DirectoryNotFoundException("Expected directory not found: " + fullDir);
            }

            Walk(fullDir, root, relativePaths);
        }

        foreach (var file in Directory.EnumerateFiles(root, "docker-compose*", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(root, "Dockerfile*", SearchOption.TopDirectoryOnly)))
        {
            relativePaths.Add(RepositoryScan.Normalize(Path.GetRelativePath(root, file)));
        }

        return relativePaths
            .Distinct(StringComparer.Ordinal)
            .Where(ShouldScan)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (p, RepositoryScan.ReadText(p)));
    }

    private static void Walk(string dir, string root, List<string> paths)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            paths.Add(RepositoryScan.Normalize(Path.GetRelativePath(root, file)));
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                Walk(sub, root, paths);
            }
        }
    }

    private static bool ShouldScan(string path)
    {
        if (path.StartsWith("src/Autheris.Api/Assets/swagger-ui/", StringComparison.Ordinal)
            || path.StartsWith("deploy/results/", StringComparison.Ordinal)
            || path.StartsWith("deploy/certs/", StringComparison.Ordinal)
            || (path.StartsWith("deploy/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name);
        return TextExtensions.Contains(ext)
            || name.StartsWith("Dockerfile", StringComparison.Ordinal)
            || name.StartsWith("Containerfile", StringComparison.Ordinal)
            || name.StartsWith("docker-compose", StringComparison.Ordinal)
            || string.Equals(name, "Makefile", StringComparison.Ordinal);
    }
}
