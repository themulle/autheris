using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-X0z (WP-F5): Oracle integration tests resolve the connection factory, the session initializer and the binder from the
/// real container. A hand-made <c>OracleConnection</c> or <c>OracleCommand</c> is limited to fixture setup, so a test cannot
/// silently bypass the pinning, the <c>BindByName</c> wrapper or the session initialization.
/// </summary>
public sealed class OracleIntegrationTestArchitectureTests
{
    private const string FixtureFile = "OracleTestFixtures.cs";

    private static string IntegrationTestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return Path.Combine(dir.FullName, "tests", "Autheris.Tests.Integration");
    }

    [Fact]
    public void RawOracleDriverTypes_InIntegrationTests_AreLimitedToTheFixtureFile()
    {
        var violations = Directory.EnumerateFiles(IntegrationTestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !string.Equals(Path.GetFileName(f), FixtureFile, StringComparison.Ordinal))
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                return text.Contains("new OracleCommand(", StringComparison.Ordinal) ||
                       text.Contains("new OracleConnection(", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        violations.ShouldBeEmpty($"These integration tests create a raw Oracle connection or command outside {FixtureFile}: {string.Join(", ", violations)}");
    }
}
