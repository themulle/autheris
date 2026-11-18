using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-25-06: shipped Helm and compose files never mount a modules volume writable.
/// The Helm readOnly check is a heuristic: it looks for <c>readOnly: true</c> within the 6 lines after the volume name.
/// </summary>
public sealed partial class ModuleVolumeReadOnlyTests
{
    [GeneratedRegex(@"^\s*-\s*name:\s*\S*modules\S*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HelmModulesMount();

    [GeneratedRegex(@"^\s*-\s*[""']?(?<spec>[^""'\s]*modules[^""'\s]*:[^""'\s]+)[""']?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ComposeModulesMount();

    [Fact]
    [Trait("Category", "Security")]
    public void Helm_modules_volume_is_read_only()
    {
        var root = RepositoryScan.RepoRoot;
        var violations = new List<string>();

        var helm = Path.Combine(root, "deploy", "helm");
        foreach (var file in Directory.EnumerateFiles(helm, "*.yaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!HelmModulesMount().IsMatch(lines[i]))
                {
                    continue;
                }

                var window = string.Join('\n', lines.Skip(i).Take(6));
                if (!Regex.IsMatch(window, @"readOnly:\s*true"))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        var composeFiles = Directory.EnumerateFiles(Path.Combine(root, "deploy"), "*compose*.y*ml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "docker-compose*.yml", SearchOption.TopDirectoryOnly));
        foreach (var file in composeFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = ComposeModulesMount().Match(lines[i]);
                if (match.Success && !match.Groups["spec"].Value.EndsWith(":ro", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        violations.ShouldBeEmpty("modules volumes must be mounted read-only");

        File.ReadAllText(Path.Combine(helm, "autheris", "values.yaml"))
            .ShouldMatch(@"readOnlyRootFilesystem:\s*true");
    }
}
