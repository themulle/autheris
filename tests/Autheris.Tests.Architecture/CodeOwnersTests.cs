using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed partial class CodeOwnersTests(ITestOutputHelper output)
{
    public sealed record CodeOwnersEntry(string Pattern, IReadOnlyList<string> Owners, string? SectionHeader);

    [GeneratedRegex(@"backup:\s*(@\w+|vacant)\b")]
    private static partial Regex BackupHeaderRegex();

    internal static List<CodeOwnersEntry> ParseCodeOwners(string text)
    {
        var entries = new List<CodeOwnersEntry>();
        string? currentSection = null;

        var lines = Regex.Split(text, "\r\n|\r|\n");
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("# subsystem:", StringComparison.Ordinal) ||
                    line.StartsWith("# security:", StringComparison.Ordinal))
                {
                    currentSection = line;
                }
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var pattern = parts[0];
                var owners = parts.Skip(1).ToList();
                entries.Add(new CodeOwnersEntry(pattern, owners, currentSection));
            }
        }

        return entries;
    }

    internal static Regex PatternToRegex(string pattern)
    {
        var p = pattern.Trim();
        var commentIdx = p.IndexOf('#');
        if (commentIdx >= 0)
        {
            p = p[..commentIdx].Trim();
        }

        var anchored = false;
        if (p.StartsWith('/'))
        {
            anchored = true;
            p = p[1..];
        }
        else if (p.Contains('/'))
        {
            anchored = true;
        }

        var isDir = false;
        if (p.EndsWith("/**", StringComparison.Ordinal))
        {
            p = p[..^3];
            isDir = true;
        }
        else if (p.EndsWith('/'))
        {
            p = p[..^1];
            isDir = true;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < p.Length; i++)
        {
            if (i + 1 < p.Length && p[i] == '*' && p[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (p[i] == '*')
            {
                sb.Append("[^/]*");
            }
            else if (p[i] == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(p[i].ToString()));
            }
        }

        var res = sb.ToString();
        string patternRx;
        if (isDir)
        {
            patternRx = anchored ? $"^{res}(/.*)?$" : $"(^|.*/){res}(/.*)?$";
        }
        else
        {
            patternRx = anchored ? $"^{res}$" : $"(^|.*/){res}$";
        }

        return new Regex(patternRx, RegexOptions.CultureInvariant);
    }

    internal static CodeOwnersEntry? MatchPath(string path, IEnumerable<CodeOwnersEntry> entries)
    {
        CodeOwnersEntry? lastMatch = null;
        var normalized = RepositoryScan.Normalize(path).Trim('/');
        foreach (var entry in entries)
        {
            var rx = PatternToRegex(entry.Pattern);
            if (rx.IsMatch(normalized))
            {
                lastMatch = entry;
            }
        }

        return lastMatch;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Every_src_and_module_folder_has_an_owner()
    {
        var repoRoot = RepositoryScan.RepoRoot;
        var targetDirs = new List<string>();

        void CollectSubdirs(string relativeParent)
        {
            var full = Path.Combine(repoRoot, relativeParent);
            if (!Directory.Exists(full))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(full))
            {
                var name = Path.GetFileName(dir);
                if (name is "bin" or "obj" or "Properties" or ".git")
                {
                    continue;
                }

                var rel = RepositoryScan.Normalize(Path.GetRelativePath(repoRoot, dir));
                targetDirs.Add(rel);
            }
        }

        CollectSubdirs("src");
        CollectSubdirs("src/Autheris.Application");
        CollectSubdirs("src/Autheris.Api");
        CollectSubdirs("src/Autheris.Extensions");
        CollectSubdirs("modules");

        targetDirs.ShouldNotBeEmpty();

        var codeOwnersContent = RepositoryScan.ReadText(".github/CODEOWNERS");
        var entries = ParseCodeOwners(codeOwnersContent);
        var nonFallback = entries.Where(e => e.Pattern != "*").ToList();

        var uncovered = new List<string>();
        foreach (var dir in targetDirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            var match = MatchPath(dir, nonFallback);
            if (match == null)
            {
                uncovered.Add(dir);
            }
            else
            {
                output.WriteLine($"Directory '{dir}' owned by '{match.Pattern}' in section '{match.SectionHeader}'");
            }
        }

        uncovered.ShouldBeEmpty($"Uncovered directories without non-fallback CODEOWNERS entry: {string.Join(", ", uncovered)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Every_entry_declares_a_backup()
    {
        var codeOwnersContent = RepositoryScan.ReadText(".github/CODEOWNERS");
        var entries = ParseCodeOwners(codeOwnersContent);

        entries.ShouldNotBeEmpty();

        foreach (var entry in entries)
        {
            entry.SectionHeader.ShouldNotBeNull($"Entry '{entry.Pattern}' must belong to a section with a declared backup.");
            BackupHeaderRegex().IsMatch(entry.SectionHeader).ShouldBeTrue(
                $"Section header '{entry.SectionHeader}' for entry '{entry.Pattern}' must declare a backup (@handle or vacant).");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Current_security_entries_are_preserved()
    {
        string[] originalEntries = [
            "src/Autheris.Api/Security/**       @themulle",
            ".github/**                         @themulle",
            "deploy/**                          @themulle",
            "Dockerfile*                        @themulle",
            "Directory.Build.props              @themulle",
            "NuGet.config                       @themulle",
            "**/packages.lock.json              @themulle",
            "tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences*.json  @themulle",
            "src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs  @themulle",
            "src/TrinoSqlEngine/TimeZones/**  @themulle",
            "tests/TrinoSqlEngine.Tests/TimeZones/Cldr/**  @themulle",
            "tests/TrinoSqlEngine.Tests/TimeZones/IanaWindowsZonesGenerator.cs  @themulle",
            "src/Autheris.Infrastructure/Persistence/Migrations/**                        @themulle",
            "tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/**               @themulle",
            "tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/**                  @themulle"
        ];

        var content = RepositoryScan.ReadText(".github/CODEOWNERS");
        foreach (var orig in originalEntries)
        {
            content.Contains(orig).ShouldBeTrue($"CODEOWNERS must preserve original security entry verbatim: '{orig}'");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_paths_are_owned()
    {
        string[] sampleSecurityPaths = [
            "src/Autheris.Infrastructure/Persistence/AuditChainAnchorStores.cs",
            "src/Autheris.Api/Hosting/FederationMaskingStartupCheck.cs",
            ".github/workflows/ci.yml",
            "deploy/podman-compose.yaml",
            "docs/adr/ADR-019-sql-engine-build-vs-buy.md",
            "src/Autheris.Api/Security/ApiKeyAuthenticationHandler.cs",
            "src/Autheris.Application/Security/SecurityMarker.cs",
            "src/TrinoSqlEngine/Ast/Security/SecurityCoverageVerifier.cs",
            "tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json",
            "src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs",
            "src/TrinoSqlEngine/TimeZones/TzdbZoneTable.cs",
            "tests/TrinoSqlEngine.Tests/TimeZones/Cldr/windowsZones.xml",
            "tests/TrinoSqlEngine.Tests/TimeZones/IanaWindowsZonesGenerator.cs",
            "src/Autheris.Infrastructure/Persistence/Migrations/20261105_Init.cs",
            "tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/baseline.json",
            "tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/steps.json",
            "tests/Autheris.Tests.Unit/Security/AuthPluginSecurityTests.cs",
            "Dockerfile",
            "Directory.Build.props",
            "NuGet.config",
            "src/Autheris.Api/packages.lock.json"
        ];

        var content = RepositoryScan.ReadText(".github/CODEOWNERS");
        var entries = ParseCodeOwners(content);

        foreach (var path in sampleSecurityPaths)
        {
            var match = MatchPath(path, entries);
            match.ShouldNotBeNull($"Path '{path}' must be matched in CODEOWNERS");
            match.SectionHeader.ShouldNotBeNull();
            match.SectionHeader.Contains("security: SEC-SIMPL-71")
                .ShouldBeTrue($"Path '{path}' resolved to entry '{match.Pattern}' in section '{match.SectionHeader}', but must resolve to SEC-SIMPL-71 security section");
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("docs/*.md", "docs/index.md", true)]
    [InlineData("docs/*.md", "docs/sub/index.md", false)]
    [InlineData("src/**/Security/**", "src/Autheris.Api/Security/Token.cs", true)]
    [InlineData("Dockerfile*", "Dockerfile", true)]
    [InlineData("Dockerfile*", "Dockerfile.dev", true)]
    [InlineData("Dockerfile*", "sub/Dockerfile", true)]
    [InlineData("/build.sh", "build.sh", true)]
    [InlineData("/build.sh", "sub/build.sh", false)]
    [InlineData("**/packages.lock.json", "src/Autheris.Api/packages.lock.json", true)]
    [InlineData("src/Autheris.Application/ConsentRequests/", "src/Autheris.Application/ConsentRequests/Service.cs", true)]
    [InlineData("src/Autheris.Application/ConsentRequests/", "src/Autheris.Application/ConsentRequests", true)]
    public void Glob_matching_follows_codeowners_semantics(string pattern, string path, bool shouldMatch)
    {
        var rx = PatternToRegex(pattern);
        rx.IsMatch(path).ShouldBe(shouldMatch);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Glob_matching_last_match_wins()
    {
        var content = """
            # subsystem: default | primary: @themulle | backup: vacant
            * @fallback
            # subsystem: src | primary: @themulle | backup: vacant
            src/** @src-owner
            # subsystem: api | primary: @themulle | backup: vacant
            src/Autheris.Api/** @api-owner
            """;
        var entries = ParseCodeOwners(content);
        var match = MatchPath("src/Autheris.Api/Controllers/Test.cs", entries);
        match.ShouldNotBeNull();
        match.Owners.ShouldContain("@api-owner");
    }
}
