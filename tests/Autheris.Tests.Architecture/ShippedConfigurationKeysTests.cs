using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-06 (SC-07): the Gateway fails startup on an unknown key, so every configuration the repository ships must only use known keys:
/// appsettings*.json, <c>Gateway__*</c> environment variables in Helm, compose files, Dockerfiles, deploy and benchmark scripts, and the
/// <c>Gateway:*</c> settings that the test hosts inject. A deleted option left behind in any of them would stop that deployment from starting.
/// </summary>
public sealed partial class ShippedConfigurationKeysTests
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", "plans"];

    private static IEnumerable<string> Files(params string[] roots)
    {
        foreach (var root in roots)
        {
            var path = Path.Combine(RepositoryScan.RepoRoot, root);
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Walk(path))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> Walk(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return file;
        }

        foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in Walk(sub))
                {
                    yield return file;
                }
            }
        }
    }

    private static IConfiguration Config(IEnumerable<string> keys) =>
        new ConfigurationBuilder().AddInMemoryCollection(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(k => k, _ => (string?)"x", StringComparer.OrdinalIgnoreCase)).Build();

    private static void Flatten(JsonElement element, string prefix, List<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name, keys);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, prefix + ":" + i++, keys);
                }

                break;
            default:
                keys.Add(prefix);
                break;
        }
    }

    [Fact]
    public void AppSettingsFiles_ContainOnlyKnownGatewayKeys()
    {
        var files = Files("src", "deploy", "benchmarks", "tests")
            .Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        files.Count.ShouldBeGreaterThanOrEqualTo(4, "The scanner must find the shipped appsettings files.");

        var problems = new List<string>();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var keys = new List<string>();
            Flatten(document.RootElement, string.Empty, keys);
            var unknown = UnknownGatewayKeyGuard.FindUnknownKeys(Config(keys));
            problems.AddRange(unknown.Select(k => $"{Path.GetRelativePath(RepositoryScan.RepoRoot, file)}: {k}"));
        }

        problems.ShouldBeEmpty();
    }

    [GeneratedRegex(@"\bGateway__[A-Za-z0-9_]*[A-Za-z0-9](?:__\{\{[^}]*\}\})?(?![A-Za-z0-9_$\{])")]
    private static partial Regex EnvironmentVariable();

    [Fact]
    public void DeploymentArtifacts_ContainOnlyKnownGatewayEnvironmentVariables()
    {
        var files = Files("deploy", "benchmarks", "docker-compose.yml", "Dockerfile", "Dockerfile.production", "Dockerfile.staging", "scripts", ".github")
            .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            .Where(f => new FileInfo(f).Length < 2_000_000)
            .ToList();
        files.Count.ShouldBeGreaterThan(10, "The scanner must find the deployment artifacts.");

        var found = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            var keys = new List<string>();
            foreach (Match match in EnvironmentVariable().Matches(text))
            {
                var name = Regex.Replace(match.Value, @"__\{\{[^}]*\}\}", "__0");
                keys.Add(name.Replace("__", ":", StringComparison.Ordinal));
            }

            if (keys.Count == 0)
            {
                continue;
            }

            found += keys.Count;
            problems.AddRange(UnknownGatewayKeyGuard.FindUnknownKeys(Config(keys)).Select(k => $"{Path.GetRelativePath(RepositoryScan.RepoRoot, file)}: {k}"));
        }

        found.ShouldBeGreaterThan(20, "The scanner must find Gateway__ environment variables in the deployment artifacts.");
        problems.ShouldBeEmpty();
    }

    [GeneratedRegex("(?:UseSetting\\(\\s*|\\[\\s*|\\(\\s*)\"(Gateway:[A-Za-z0-9_:]*[A-Za-z0-9_])\"")]
    private static partial Regex TestHostSetting();

    [Fact]
    public void TestHostSettings_ContainOnlyKnownGatewayKeys()
    {
        var problems = new List<string>();
        var found = 0;
        // Unit and architecture tests feed in-memory configuration to isolated components (and to the guard itself); only the hosts count.
        foreach (var file in Files("tests")
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains("Autheris.Tests.Unit", StringComparison.Ordinal) && !f.Contains("Autheris.Tests.Architecture", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var keys = TestHostSetting().Matches(text).Select(m => m.Groups[1].Value).ToList();
            if (keys.Count == 0)
            {
                continue;
            }

            found += keys.Count;
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (UnknownGatewayKeyGuard.FindUnknownKeys(Config([key])).Count > 0)
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryScan.RepoRoot, file)}: {key}");
                }
            }
        }

        found.ShouldBeGreaterThan(50, "The scanner must find the settings the test hosts inject.");
        problems.ShouldBeEmpty();
    }

    [GeneratedRegex("\"(Gateway:[A-Za-z0-9_]+(?::[A-Za-z0-9_]+)+)\"")]
    private static partial Regex ConfigurationKeyLiteral();

    /// <summary>
    /// A complete <c>Gateway:...</c> key literal in production code is a raw configuration read. It must be declared by an options type or be
    /// listed in <see cref="GatewayConfigurationSchema.RawReadKeys"/>; otherwise the guard would reject a key the code really reads.
    /// </summary>
    [Fact]
    public void SourceConfigurationKeyLiterals_AreKnownToTheSchema()
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.ProductionFiles.Where(f => !f.RelativePath.EndsWith("/GatewaySections.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in ConfigurationKeyLiteral().Matches(file.Text))
            {
                var key = match.Groups[1].Value;
                if (UnknownGatewayKeyGuard.FindUnknownKeys(Config([key])).Count > 0)
                {
                    problems.Add($"{key} ({file.RelativePath})");
                }
            }
        }

        problems.ShouldBeEmpty();
    }
}
