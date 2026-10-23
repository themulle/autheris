using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-R100-02 and SEC-R100-09: the Robin 100-scenario extension adds no test outside the guarded <c>RobinScenarios</c> class and never
/// registers a known gap for a denied-request, masking, scope or leak cell. The scanners fail closed (no files or no dictionary found is a failure).
/// </summary>
public sealed partial class Robin100ArchitectureTests
{
    private const string RobinDir = "tests/Autheris.Tests.Parity/Robin";

    [GeneratedRegex(@"^\s*\[(Fact|Theory|Trait|Collection|ParityFact|ParityTheory)\b", RegexOptions.Multiline)]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"\[\s*""(?<key>[^""]+)""\s*\]\s*=")]
    private static partial Regex GapKeyPattern();

    [GeneratedRegex(@"partial\s+class\s+RobinScenarios[ \t]*(\(|:)", RegexOptions.Multiline)]
    private static partial Regex PartialWithBaseOrCtor();

    [GeneratedRegex(@"denied|mask|leak|scope|requiredfilter|readonly|write|forbidden|inject|cursor|bulk|analyze|existence|oracle", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityCellName();

    /// <summary>Attribute lines in a new part that would add a test class-level or unguarded test: everything except <c>ParityFact</c>/<c>ParityTheory</c>.</summary>
    internal static IReadOnlyList<string> UnguardedAttributes(string source) =>
        AttributePattern().Matches(source).Select(m => m.Groups[1].Value).Where(a => a is not ("ParityFact" or "ParityTheory")).ToList();

    internal static IReadOnlyList<string> SecurityGapKeys(string knownGapsSource)
    {
        var keys = GapKeyPattern().Matches(knownGapsSource).Select(m => m.Groups["key"].Value).ToList();
        return keys.Where(k => SecurityCellName().IsMatch(k.Split('|')[0])).ToList();
    }

    private static string[] NewParts()
    {
        var dir = Path.Combine(ParityIsolationArchitectureTests.FindRepoRoot(), RobinDir);
        Directory.Exists(dir).ShouldBeTrue("Robin directory not found: the guard fails closed.");
        return Directory.GetFiles(dir, "RobinScenarios.Wp*.cs");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_NewPartsAddNoUnguardedTests()
    {
        var parts = NewParts();
        parts.Length.ShouldBeGreaterThan(0, "no RobinScenarios.Wp*.cs part found: vacuous guard.");
        foreach (var part in parts)
        {
            var source = File.ReadAllText(part);
            UnguardedAttributes(source).ShouldBeEmpty($"{Path.GetFileName(part)} (SEC-R100-09): only ParityFact/ParityTheory inside RobinScenarios.");
            PartialWithBaseOrCtor().IsMatch(source).ShouldBeFalse($"{Path.GetFileName(part)}: a part must not repeat the base list or constructor.");
            source.ShouldContain("partial class RobinScenarios");
        }

        // Negative self-checks.
        UnguardedAttributes("    [Fact]\n    public void X() {}").ShouldBe(["Fact"]);
        UnguardedAttributes("    [Trait(\"Category\", \"Parity\")]").ShouldBe(["Trait"]);
        UnguardedAttributes("    [ParityTheory]\n    [InlineData(\"a\")]").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Robin100_NoKnownGapForDeniedOrLeakCells()
    {
        var path = Path.Combine(ParityIsolationArchitectureTests.FindRepoRoot(), RobinDir, "RobinKnownGaps.cs");
        File.Exists(path).ShouldBeTrue("RobinKnownGaps.cs not found: the guard fails closed.");
        var source = File.ReadAllText(path);
        source.ShouldContain("Gaps", Case.Sensitive, "known gap dictionary not found: vacuous guard.");
        SecurityGapKeys(source).ShouldBeEmpty("SEC-R100-02: a denied/leak/masking/scope cell must never be a known gap.");

        // Negative self-check.
        SecurityGapKeys("[\"Robin_92_MaskedAmountDenied|GraphQl|\"] = \"PARITY-GAP-050\",").Count.ShouldBe(1);
        SecurityGapKeys("[\"Robin_36_TimestampFilterIso|Rest|\"] = \"PARITY-GAP-030\",").ShouldBeEmpty();
    }
}
