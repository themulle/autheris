using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class NoReadyToRunArchitectureTests
{
    [Fact]
    public void No_project_or_props_file_enables_PublishReadyToRun()
    {
        var relativePaths = new List<string>(RepositoryScan.EnumerateFiles("", "*.csproj"));

        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            if (File.Exists(Path.Combine(RepositoryScan.RepoRoot, name)))
            {
                relativePaths.Add(name);
            }
        }

        var violations = new List<string>();

        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(RepositoryScan.RepoRoot, relativePath);
            var doc = XDocument.Load(fullPath);
            var r2rElements = doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "PublishReadyToRun", StringComparison.OrdinalIgnoreCase));

            foreach (var elem in r2rElements)
            {
                if (string.Equals(elem.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{relativePath}: <{elem.Name.LocalName}>{elem.Value.Trim()}</{elem.Name.LocalName}>");
                }
            }
        }

        violations.ShouldBeEmpty();
    }
}
