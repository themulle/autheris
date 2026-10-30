using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #388 (WP-388-3, plan D-10, I388-SC-13): the internal test seams of the governance migration runner can bend the fail-closed
/// behaviour (pretend to be Production, hide a privileged login, swallow the runtime-DDL audit, inject extra steps). They exist for tests
/// only, so no production code under <c>src/</c> may ever assign them.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class GovernanceMigrationTestSeamTests
{
    /// <summary>Seams that production code must never set at all.</summary>
    private static readonly string[] NeverAssigned =
    [
        "RuntimeLoginRunsDdlOverride",
        "ExcessPrivilegeOverride",
        "RuntimeDdlAuditOverride",
        "AdditionalSteps",
        "StepVersionCapOverride"
    ];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static IEnumerable<(string File, int Line, string Text)> SourceLines()
    {
        var src = Path.Combine(Root(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root(), file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                yield return (relative, i + 1, lines[i]);
            }
        }
    }

    [GeneratedRegex(@"\b(?<name>[A-Za-z]+)\s*=(?!=|>)")]
    private static partial Regex AssignmentPattern();

    private static bool IsDeclaration(string text) => text.Contains("{ get; init; }", StringComparison.Ordinal);

    private static bool AssignsOutsideDeclaration(string text, string name) =>
        !IsDeclaration(text)
        && !text.TrimStart().StartsWith("//", StringComparison.Ordinal)
        && !text.TrimStart().StartsWith("///", StringComparison.Ordinal)
        && AssignmentPattern().Matches(text).Any(m => m.Groups["name"].Value == name);

    [Fact]
    public void I388_SC_13_TestSeams_AreNeverAssignedInProductionCode()
    {
        var offenders = SourceLines()
            .Where(l => NeverAssigned.Any(seam => AssignsOutsideDeclaration(l.Text, seam)))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty("internal test seams must only be set by tests:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void I388_SC_13_IsProduction_IsOnlyDerivedFromTheHostEnvironmentByTheEngineRunners()
    {
        var offenders = SourceLines()
            .Where(l => AssignsOutsideDeclaration(l.Text, "IsProduction"))
            .Where(l => !(
                (l.File.EndsWith("Migrations/PostgreSqlGovernanceMigrationRunner.cs", StringComparison.Ordinal)
                    || l.File.EndsWith("Migrations/SqlServerGovernanceMigrationRunner.cs", StringComparison.Ordinal))
                && l.Text.Contains("IsProduction = environment is not null &&", StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty("IsProduction must come from IHostEnvironment inside the runner constructors only:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void I388_SC_13_SeamScan_SeesTheDeclarationsAndTheRunnerWiring()
    {
        // Guards the scan itself: it must find the seams it protects, otherwise a rename would silently empty the check.
        var all = string.Join('\n', SourceLines().Select(l => l.Text));
        foreach (var seam in NeverAssigned.Append("IsProduction"))
        {
            all.ShouldContain(seam + " { get; init; }", Case.Sensitive, $"seam '{seam}' was renamed or removed: update this test");
        }

        SourceLines().Count(l => AssignsOutsideDeclaration(l.Text, "IsProduction")).ShouldBe(2);
    }
}
