using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
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
}
