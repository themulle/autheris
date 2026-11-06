using Autheris.Api.Mcp;
using Autheris.Infrastructure.Docs;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-ARC-B-10: Verifies package boundary diet and adapter placement for <c>Autheris.Application</c>.
/// </summary>
public sealed class PackageBoundaryArchitectureTests
{
    [Fact]
    public void Application_ShouldNotHaveDependencyOnPrometheus()
    {
        var applicationAssembly = typeof(Autheris.Application.Services.ConsentResolutionService).Assembly;

        var result = Types.InAssembly(applicationAssembly)
            .ShouldNot()
            .HaveDependencyOn("Prometheus")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Autheris.Application must not depend on Prometheus: {string.Join(", ", result.FailingTypeNames ?? [])}");

        var referencedAssemblies = applicationAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referencedAssemblies.ShouldNotContain("Prometheus.NetStandard");
        referencedAssemblies.ShouldNotContain("prometheus-net");
    }

    [Fact]
    public void McpStdioRunner_ShouldResideInApiAssembly()
    {
        typeof(McpStdioRunner).Assembly.ShouldBe(typeof(Autheris.Api.Extensions.GatewayServiceCollectionExtensions).Assembly);
    }

    [Fact]
    public void FileSystemUniversalDocStore_ShouldResideInInfrastructureAssembly()
    {
        typeof(FileSystemUniversalDocStore).Assembly.ShouldBe(typeof(Autheris.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly);
    }

    [Fact]
    public void No_garnet_package()
    {
        var csprojFiles = RepositoryScan.EnumerateFiles("", "*.csproj");
        var violations = new System.Collections.Generic.List<string>();

        foreach (var relativePath in csprojFiles)
        {
            var fullPath = System.IO.Path.Combine(RepositoryScan.RepoRoot, relativePath);
            var content = System.IO.File.ReadAllText(fullPath);
            if (content.Contains("Microsoft.Garnet", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{relativePath}: references Microsoft.Garnet");
            }
        }

        violations.ShouldBeEmpty("Microsoft.Garnet package is prohibited across all projects.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Api_extensions_usages_do_not_grow()
    {
        var allowedFiles = RepositoryScan.ReadBaseline("api-extensions-usages.txt")
            .ToHashSet(StringComparer.Ordinal);

        var actualFiles = new System.Collections.Generic.List<string>();
        foreach (var file in RepositoryScan.EnumerateFiles("src/Autheris.Api", "*.cs"))
        {
            var text = RepositoryScan.ReadText(file);
            if (text.Contains("Autheris.Extensions", StringComparison.Ordinal))
            {
                actualFiles.Add(RepositoryScan.ToRelativePath(file));
            }
        }

        actualFiles.Count.ShouldBeLessThanOrEqualTo(
            allowedFiles.Count,
            $"Direct Autheris.Api -> Autheris.Extensions usage count ({actualFiles.Count}) exceeded baseline ({allowedFiles.Count}).");

        var unexpected = actualFiles.Where(f => !allowedFiles.Contains(f)).ToList();
        unexpected.ShouldBeEmpty(
            "New Autheris.Api files referencing Autheris.Extensions are forbidden:\n" + string.Join("\n", unexpected));
    }
}
