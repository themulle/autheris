using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-ARC-A-05: a mis-registered container must fail at startup instead of silently dropping audit or policy. The Null object test doubles for
/// the audit store, the policy enforcement and the compiled plan cache live in the test projects only; no production type references them.
/// </summary>
public sealed class NoProductionNullTestDoublesArchitectureTests
{
    private static readonly string[] TestDoubleNames =
    [
        "NullAuditLogRepository", "NullPolicyEnforcementService", "NullCompiledSqlQueryPlanCache"
    ];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static List<string> FilesReferencing(string folder) =>
        SourceFiles(folder)
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                return TestDoubleNames.Any(n => text.Contains(n, StringComparison.Ordinal));
            })
            .Select(p => Path.GetRelativePath(RepositoryRoot(), p))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void NoProductionTypeReferencesNullTestDoubles()
    {
        var offenders = FilesReferencing("src");
        offenders.ShouldBeEmpty("Production code must not reference test doubles that silently drop audit or policy: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NullTestDoubles_ExistInTheUnitTestProject_PositiveControl()
    {
        // The scan works on real files: the doubles are found where they now live.
        FilesReferencing("tests/Autheris.Tests.Unit/TestDoubles").Count.ShouldBeGreaterThanOrEqualTo(2);
    }
}
