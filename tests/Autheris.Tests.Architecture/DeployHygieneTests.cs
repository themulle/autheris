using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>SEC-SIMPL-05: shipped deploy files carry no literal database password (only env/secret references or placeholders).</summary>
public sealed partial class DeployHygieneTests
{
    private static readonly string[] Extensions = [".yaml", ".yml", ".json", ".sh", ".ps1", ".py", ".tpl", ".env", ".example"];

    [GeneratedRegex(@"(?:Password|Pwd)\s*=\s*(?<v>[^;""'\s]+)|POSTGRES_PASSWORD\s*[:=]\s*(?<v>[^\s#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordAssignment();

    [Fact]
    [Trait("Category", "Security")]
    public void Deploy_files_contain_no_default_database_password()
    {
        var deploy = Path.Combine(RepositoryScan.RepoRoot, "deploy");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(deploy, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepositoryScan.RepoRoot, file).Replace('\\', '/');
            if (relative.StartsWith("deploy/results/", StringComparison.Ordinal)
                || !Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                foreach (Match match in PasswordAssignment().Matches(line))
                {
                    if (!IsReferenceOrPlaceholder(match.Groups["v"].Value))
                    {
                        violations.Add($"{relative}:{number}");
                    }
                }
            }
        }

        violations.ShouldBeEmpty("deploy files must reference secrets via environment/secret references, never literals");
    }

    private static bool IsReferenceOrPlaceholder(string value) =>
        value.Length == 0
        || value.StartsWith("${", StringComparison.Ordinal)
        || value.StartsWith("$", StringComparison.Ordinal)
        || value.StartsWith("{{", StringComparison.Ordinal)
        || value.StartsWith("<", StringComparison.Ordinal)
        || value.Contains("$(", StringComparison.Ordinal)
        || value.StartsWith("os.environ", StringComparison.Ordinal)
        || value.StartsWith("\"${", StringComparison.Ordinal)
        || value.Contains("OVERRIDDEN_VIA_ENV", StringComparison.Ordinal)
        || value.StartsWith("CHANGE_ME", StringComparison.Ordinal);
}
