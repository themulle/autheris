using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class ReleaseVersionArchitectureTests
{
    [Fact]
    public void Directory_build_props_holds_no_product_version()
    {
        var propsPath = Path.Combine(RepositoryScan.RepoRoot, "Directory.Build.props");
        File.Exists(propsPath).ShouldBeTrue();

        var xml = XDocument.Load(propsPath);
        var versionElements = xml.Descendants("Version").ToList();
        versionElements.Count.ShouldBe(1);

        var versionEl = versionElements[0];
        versionEl.Value.Trim().ShouldBe("0.0.0-dev");
        versionEl.Attribute("Condition")?.Value.Trim().ShouldBe("'$(Version)' == ''");

        var csprojFiles = RepositoryScan.EnumerateFiles("", "*.csproj")
            .Select(p => Path.Combine(RepositoryScan.RepoRoot, p))
            .ToList();

        csprojFiles.ShouldNotBeEmpty();

        var versionElementNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Version", "AssemblyVersion", "FileVersion", "InformationalVersion"
        };

        var violatingFiles = new List<string>();
        foreach (var file in csprojFiles)
        {
            var projectXml = XDocument.Load(file);
            var violations = projectXml.Descendants()
                .Where(e => versionElementNames.Contains(e.Name.LocalName))
                .ToList();

            if (violations.Count > 0)
            {
                violatingFiles.Add(Path.GetRelativePath(RepositoryScan.RepoRoot, file));
            }
        }

        violatingFiles.ShouldBeEmpty("No *.csproj project should explicitly declare version elements.");
    }

    [Fact]
    public void Chart_versions_are_placeholders()
    {
        var chartPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "Chart.yaml");
        File.Exists(chartPath).ShouldBeTrue();

        var lines = File.ReadAllLines(chartPath);
        var versionLine = lines.FirstOrDefault(l => l.StartsWith("version:", StringComparison.Ordinal));
        var appVersionLine = lines.FirstOrDefault(l => l.StartsWith("appVersion:", StringComparison.Ordinal));

        versionLine.ShouldNotBeNull();
        appVersionLine.ShouldNotBeNull();

        versionLine.Split(':')[1].Trim().ShouldBe("0.0.0-dev");
        appVersionLine.Split(':')[1].Trim().Trim('"').ShouldBe("0.0.0-dev");
    }
}
