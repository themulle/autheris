namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AnalyzerConfigurationTests
{
    private const string PragmaAllowlistRelativePath =
        "tests/Autheris.Tests.Architecture/Baselines/culture-analyzer-pragma-exemptions.txt";
    private const int InitialPragmaAllowlistCeiling = 0;

    private static readonly string[] RequiredRules = ["CA1307", "CA1309", "CA1310"];

    [GeneratedRegex(@"^\s*#pragma\s+warning\s+disable\b(?<rest>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PragmaDisableRegex();

    [Fact]
    public void Culture_analyzers_are_errors_in_src()
    {
        var root = RepositoryScan.FindRepositoryRoot();

        var propsPath = Path.Combine(root, "Directory.Build.props");
        File.Exists(propsPath).ShouldBeTrue("Directory.Build.props must exist at repository root.");

        var propsDoc = XDocument.Load(propsPath);
        var noWarnTokens = propsDoc.Descendants("NoWarn")
            .SelectMany(el => el.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in RequiredRules)
        {
            noWarnTokens.ShouldNotContain(
                rule,
                $"SEC-OI-613: Directory.Build.props <NoWarn> must not suppress {rule}.");
        }

        var editorConfigPath = Path.Combine(root, ".editorconfig");
        File.Exists(editorConfigPath).ShouldBeTrue(".editorconfig must exist at repository root.");

        var sections = ParseEditorConfigSections(File.ReadAllLines(editorConfigPath));
        var srcSections = sections
            .Where(s => IsSrcSectionHeader(s.Header))
            .ToList();

        srcSections.ShouldNotBeEmpty("SEC-OI-613-4: .editorconfig must contain a [src/**.cs] section.");

        foreach (var rule in RequiredRules)
        {
            var key = $"dotnet_diagnostic.{rule}.severity";
            var effective = srcSections
                .SelectMany(s => s.Entries)
                .LastOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

            effective.Key.ShouldNotBeNull($"SEC-OI-613-4: .editorconfig [src/**.cs] must configure {key} = error.");
            string.Equals(effective.Value, "error", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
                $"SEC-OI-613-4: .editorconfig {key} under src must be 'error', found '{effective.Value}'.");
        }
    }

    [Fact]
    public void Culture_pragmas_only_in_allowlisted_files()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var allowlist = LoadAllowlist(root);

        allowlist.Count.ShouldBeLessThanOrEqualTo(
            InitialPragmaAllowlistCeiling,
            $"SEC-OI-613-4: {PragmaAllowlistRelativePath} is a shrink-only allowlist (ceiling {InitialPragmaAllowlistCeiling}, actual {allowlist.Count}).");

        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            foreach (Match match in PragmaDisableRegex().Matches(file.Text))
            {
                var rest = match.Groups["rest"].Value;
                var matchedRules = RequiredRules.Where(r => rest.Contains(r, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchedRules.Count == 0)
                {
                    continue;
                }

                if (!allowlist.ContainsKey(file.RelativePath))
                {
                    violations.Add($"{file.RelativePath} ({string.Join(", ", matchedRules)})");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"SEC-OI-613-4: #pragma warning disable for CA1307/CA1309/CA1310 is forbidden in src/ outside {PragmaAllowlistRelativePath}: {string.Join(", ", violations)}");
    }

    private static bool IsSrcSectionHeader(string header) =>
        header.StartsWith("[src/", StringComparison.OrdinalIgnoreCase) &&
        header.EndsWith(".cs]", StringComparison.OrdinalIgnoreCase);

    private static List<(string Header, List<(string Key, string Value)> Entries)> ParseEditorConfigSections(string[] lines)
    {
        var sections = new List<(string Header, List<(string Key, string Value)> Entries)>();
        var currentHeader = string.Empty;
        var currentEntries = new List<(string Key, string Value)>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                if (currentHeader.Length > 0)
                {
                    sections.Add((currentHeader, currentEntries));
                }

                currentHeader = line;
                currentEntries = [];
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && currentHeader.Length > 0)
            {
                currentEntries.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
        }

        if (currentHeader.Length > 0)
        {
            sections.Add((currentHeader, currentEntries));
        }

        return sections;
    }

    private static Dictionary<string, string> LoadAllowlist(string root)
    {
        var path = Path.Combine(root, PragmaAllowlistRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Missing baseline file: {PragmaAllowlistRelativePath}");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('\t', 2);
            parts.Length.ShouldBe(2, $"Malformed allowlist line in {PragmaAllowlistRelativePath}: '{raw}'");
            var relPath = RepositoryScan.Normalize(parts[0].Trim());
            var reason = parts[1].Trim();
            relPath.ShouldNotBeEmpty();
            reason.ShouldNotBeEmpty();
            map[relPath] = reason;
        }

        return map;
    }
}
