using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// PLAN-SEC-OLAP-01 (plan test 30): the OLAP engine is reached through the staging service only, the generated-SQL entry has a single
/// caller, and the staging service owns the federation gate call. Source scans, like the other endpoint guards.
/// </summary>
[Trait("Category", "Security")]
public sealed class OlapEntryPointArchitectureTests
{
    private static string SrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Autheris.Api")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src from the test output directory.");
    }

    private static IEnumerable<string> ProductionFiles() =>
        Directory.EnumerateFiles(SrcDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Endpoints_ReachTheOlapEngineOnlyThroughTheStagingService()
    {
        var endpoints = Path.Combine(SrcDirectory(), "Autheris.Api", "Endpoints");
        var offenders = Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("IDuckDbOlapEngine", StringComparison.Ordinal) || text.Contains("ExecuteOlapQueryAsync", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void ExecuteGeneratedAsync_HasASingleCaller_TheFederatedExecutor()
    {
        var callers = ProductionFiles()
            .Where(f => File.ReadAllText(f).Contains(".ExecuteGeneratedAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        callers.ShouldBe(["FederatedDuckDbExecutionService.cs"]);
    }

    [Fact]
    public void OlapStagingService_CallsTheFederationGate_AndNoOtherOlapFileReadsTheSwitch()
    {
        var olapDir = Path.Combine(SrcDirectory(), "Autheris.Application", "Olap");
        File.ReadAllText(Path.Combine(olapDir, "OlapStagingService.cs")).ShouldContain("federationGate.EnsureAllowed(");

        var readers = Directory.EnumerateFiles(olapDir, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("CrossSource.Enabled", StringComparison.Ordinal) || File.ReadAllText(f).Contains("AllowedTransports", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        readers.ShouldBeEmpty();
    }
}
