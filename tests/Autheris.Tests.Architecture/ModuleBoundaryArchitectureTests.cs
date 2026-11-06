namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Api.Hosting;
using Autheris.Modules.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// Architectural gates for the out-of-core module host and module contracts (WP-SIMPL-25).
/// </summary>
public sealed partial class ModuleBoundaryArchitectureTests
{
    private static readonly string[]AllowedModuleProjectReferences =
    [
        "Autheris.Modules.Abstractions",
        "Autheris.Domain",
        "Autheris.Application",
    ];

    private static readonly string[] ForbiddenDataSourceConnectionTypes =
    [
        "ISqlConnectionFactory",
        "SqlConnectionFactory",
        "NpgsqlConnection",
        "SqlConnection",
        "OracleConnection",
        "SqliteConnection",
        "DuckDBConnection",
        "DatabricksStatementClient",
    ];

    [GeneratedRegex(@"<ProjectReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferenceRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void Modules_reference_only_abstractions_domain_and_application()
    {
        // 1. Autheris.Modules.Abstractions itself may only reference Autheris.Domain (and ASP.NET Core / BCL).
        var abstractionsCsproj = Path.Combine(
            RepositoryScan.FindRepositoryRoot(),
            "src",
            "Autheris.Modules.Abstractions",
            "Autheris.Modules.Abstractions.csproj");
        File.Exists(abstractionsCsproj).ShouldBeTrue("Missing src/Autheris.Modules.Abstractions/Autheris.Modules.Abstractions.csproj");

        var abstractionsRefs = ExtractProjectReferences(File.ReadAllText(abstractionsCsproj));
        abstractionsRefs.ShouldBe(["Autheris.Domain"]);

        var abstractionsAssembly = typeof(IAutherisModule).Assembly;
        var referencedAssemblyNames = abstractionsAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referencedAssemblyNames.ShouldNotContain("Autheris.Infrastructure");
        referencedAssemblyNames.ShouldNotContain("Autheris.Api");
        referencedAssemblyNames.ShouldNotContain("Autheris.Extensions");
        referencedAssemblyNames.ShouldNotContain("Autheris.GraphQL");
        referencedAssemblyNames.ShouldNotContain("FastSqlEngine");
        referencedAssemblyNames.ShouldNotContain("TrinoSqlEngine");

        // 2. Any src/Autheris.Modules.* project (or modules/*/*.csproj) may only reference Abstractions, Domain, and Application.
        var root = RepositoryScan.FindRepositoryRoot();
        var moduleProjects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("Autheris.Modules.", StringComparison.Ordinal) &&
                        !string.Equals(Path.GetFileNameWithoutExtension(p), "Autheris.Modules.Abstractions", StringComparison.Ordinal))
            .ToList();

        var modulesRootDir = Path.Combine(root, "modules");
        if (Directory.Exists(modulesRootDir))
        {
            moduleProjects.AddRange(
                Directory.EnumerateFiles(modulesRootDir, "*.csproj", SearchOption.AllDirectories)
                    .Where(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var csproj in moduleProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var refs = ExtractProjectReferences(File.ReadAllText(csproj));
            var forbidden = refs.Where(r => !AllowedModuleProjectReferences.Contains(r, StringComparer.Ordinal)).ToList();
            forbidden.ShouldBeEmpty(
                $"Module project '{projectName}' references forbidden projects: {string.Join(", ", forbidden)}. " +
                $"Modules may only reference {string.Join(", ", AllowedModuleProjectReferences)}.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Modules_open_no_data_source_connections()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var dirsToScan = new List<string>
        {
            Path.Combine(root, "src", "Autheris.Modules.Abstractions"),
        };

        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "src"), "Autheris.Modules.*", SearchOption.TopDirectoryOnly))
        {
            if (!dirsToScan.Contains(dir, StringComparer.Ordinal))
            {
                dirsToScan.Add(dir);
            }
        }

        var modulesDir = Path.Combine(root, "modules");
        if (Directory.Exists(modulesDir))
        {
            dirsToScan.Add(modulesDir);
        }

        var violations = new List<string>();
        foreach (var dir in dirsToScan)
        {
            foreach (var csFile in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, csFile).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                var content = File.ReadAllText(csFile);
                foreach (var forbidden in ForbiddenDataSourceConnectionTypes)
                {
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(forbidden)}\b"))
                    {
                        violations.Add($"{rel} references forbidden data-source connection type '{forbidden}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            "Modules and module abstractions must never open or reference governed data-source connections:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Governance_connection_factory_returns_governance_store_only()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var factoryPath = Path.Combine(root, "src", "Autheris.Api", "Hosting", "GovernanceConnectionFactory.cs");
        File.Exists(factoryPath).ShouldBeTrue("Missing src/Autheris.Api/Hosting/GovernanceConnectionFactory.cs");

        var source = File.ReadAllText(factoryPath);
        source.ShouldContain("GovernanceDb");
        source.ShouldNotContain("DataSources");
        source.ShouldNotContain("DataSourceConnectionOptions");
        source.ShouldNotContain("ISqlConnectionFactory");

        var factoryType = typeof(GovernanceConnectionFactory);
        typeof(IGovernanceConnectionFactory).IsAssignableFrom(factoryType).ShouldBeTrue();

        var fields = factoryType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            field.FieldType.Name.ShouldNotContain("DataSource");
            field.FieldType.Name.ShouldNotBe("ISqlConnectionFactory");
        }
    }

    private static List<string> ExtractProjectReferences(string csprojContent) =>
        ProjectReferenceRegex()
            .Matches(csprojContent)
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
