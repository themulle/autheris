using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Workload subject roles and static JWKS sources are configuration only: no endpoint, GraphQL mutation, MCP tool or SCIM route reads
/// them for writing, and the in-memory <c>StaticMappingRoleProvider.AddMapping</c> is never called by production code.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class SubjectRoleImmutabilityArchitectureTests
{
    private const string AddMappingDefinition = "StaticMappingRoleProvider.cs";

    [GeneratedRegex(@"\bSubjectRoles\b|\bSubjectRoleMapping\b|\bStaticJwksSource\b|\bJwks\b")]
    private static partial Regex ConfigSurfaceSymbol();

    [GeneratedRegex(@"\bAddMapping\s*\(")]
    private static partial Regex AddMappingCall();

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryScan.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void StaticMappingRoleProvider_AddMapping_HasNoSrcCallSite()
    {
        var callSites = SourceFiles()
            .Where(f => !f.EndsWith(AddMappingDefinition, StringComparison.Ordinal))
            .Where(f => AddMappingCall().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryScan.RepoRoot, f))
            .ToList();

        callSites.ShouldBeEmpty("StaticMappingRoleProvider.AddMapping must not be called from src/ (configuration is the only mapping path).");
    }

    [Fact]
    public void SubjectRoles_NotWritableByAnyEndpointOrMutation()
    {
        // Routes, GraphQL types and MCP tools never touch the mapping or key-source configuration types.
        var surfaces = new[] { "Endpoints", "GraphQL", "Mcp", "Scim" };
        var offenders = SourceFiles()
            .Where(f => surfaces.Any(s => f.Split(Path.DirectorySeparatorChar).Any(seg => seg.Contains(s, StringComparison.Ordinal))))
            .Where(f => ConfigSurfaceSymbol().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryScan.RepoRoot, f))
            .ToList();

        offenders.ShouldBeEmpty("Endpoint, GraphQL, MCP and SCIM code must not read or write SubjectRoles or Jwks.");
    }
}
