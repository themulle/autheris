using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Configuration surface budget over core <c>src/</c> (module options live outside <c>src/</c> and are not counted). The four
/// checks are shrink-only ratchets pinned at today's counts; they must be lowered whenever the surface shrinks. The final
/// reduction step replaces the ceilings with the targets below. Counting mirrors <c>tools/options-inventory/options_inventory.py</c>.
/// </summary>
public sealed partial class OptionsBudgetTests(ITestOutputHelper output)
{
    // Final targets of the configuration surface reduction. Ceilings are lowered towards these and never raised.
    internal const int TargetOptionsClasses = 90;
    internal const int TargetSettableProperties = 600;
    internal const int TargetEnabledFlags = 40;
    internal const int TargetUndocumentedProperties = 0;

    private const int CeilingOptionsClasses = 160;
    private const int CeilingSettableProperties = 925;
    private const int CeilingEnabledFlags = 64;
    private const int CeilingUndocumentedProperties = 385;

    [GeneratedRegex(@"\bclass\s+(\w+Options)\b")]
    private static partial Regex OptionsClassDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct|interface)\s+(\w+)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^\s*public\s+(?:required\s+)?(?:virtual\s+)?([\w<>\[\]?,.\s]+?)\s+(\w+)\s*\{\s*get;\s*(?:set|init);\s*\}")]
    private static partial Regex SettableProperty();

    [GeneratedRegex(@"\brecord\s+(\w+)\s*\(")]
    private static partial Regex PositionalRecordDeclaration();

    private static readonly HashSet<string> LegacyPositionalRecordsInDomainOptions = new(StringComparer.Ordinal)
    {
        "GoldenQueryDefinition",
        "ResourceGroupTierConfigOptions",
        "EffectiveDataSourceProfile",
        "DevFeatures",
    };

    internal sealed record Surface(int Classes, int Properties, int EnabledFlags, int Undocumented);

    private static readonly Lazy<Surface> Measured = new(Measure);

    [Fact]
    public void Options_classes_ratchet()
    {
        Check("Options classes", Measured.Value.Classes, CeilingOptionsClasses, TargetOptionsClasses);
    }

    [Fact]
    public void Settable_properties_ratchet()
    {
        Check("Settable option properties", Measured.Value.Properties, CeilingSettableProperties, TargetSettableProperties);
    }

    [Fact]
    public void Enabled_flags_ratchet()
    {
        Check("bool *Enabled flags", Measured.Value.EnabledFlags, CeilingEnabledFlags, TargetEnabledFlags);
    }

    [Fact]
    public void Undocumented_properties_ratchet()
    {
        Check("Undocumented option properties", Measured.Value.Undocumented, CeilingUndocumentedProperties, TargetUndocumentedProperties);
    }

    [Theory]
    [InlineData("class Foo { }", 0)]
    [InlineData("public sealed class FooOptions { }", 1)]
    [InlineData("// public sealed class FooOptions { }", 0)]
    public void Class_declaration_counting_ignores_comments(string line, int expected)
    {
        ProgramRatchetTests.CountOptionsInLine(line).ShouldBe(expected);
    }

    [Fact]
    public void Scanner_counts_documented_and_undocumented_properties()
    {
        const string source = """
            public sealed class SampleOptions
            {
                /// <summary>Documented.</summary>
                [Range(1, 2)]
                public int A { get; set; } = 1;

                public bool BEnabled { get; init; }
            }

            public sealed class Helper
            {
                public int NotCounted { get; set; }
            }
            """;
        var surface = MeasureText(source);
        surface.Properties.ShouldBe(2);
        surface.Undocumented.ShouldBe(1);
        surface.Classes.ShouldBe(1);
        surface.EnabledFlags.ShouldBe(1);
    }

    /// <summary>
    /// WP-SIMPL-32 A3.3 anti-gaming rule: merged and nested option types must stay <c>class</c> with settable
    /// properties (what the scanner counts) rather than positional <c>record</c> types whose parameters would
    /// bypass the class/property ratchets.
    /// </summary>
    [Fact]
    public void No_new_positional_records_in_domain_options()
    {
        var unexpected = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (!file.RelativePath.StartsWith("src/Autheris.Domain/Options/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in Regex.Split(file.Text, "\r\n|\r|\n"))
            {
                if (ProgramRatchetTests.IsCommentLine(line))
                {
                    continue;
                }

                var m = PositionalRecordDeclaration().Match(line);
                if (m.Success && !LegacyPositionalRecordsInDomainOptions.Contains(m.Groups[1].Value))
                {
                    unexpected.Add($"{file.RelativePath}: {m.Groups[1].Value}");
                }
            }
        }

        unexpected.ShouldBeEmpty(
            $"Positional records in src/Autheris.Domain/Options/ bypass the OptionsBudgetTests scanner; use a class with settable properties instead: {string.Join(", ", unexpected)}");
    }

    private void Check(string what, int actual, int ceiling, int target)
    {
        output.WriteLine($"{what}: actual={actual}, ceiling={ceiling}, target={target}");
        if (actual > ceiling)
        {
            actual.ShouldBeLessThanOrEqualTo(
                ceiling,
                $"{what} grew: actual {actual} > ceiling {ceiling}. Remove options instead of adding them (target {target}).");
        }
        else if (actual < ceiling)
        {
            actual.ShouldBeGreaterThanOrEqualTo(
                ceiling,
                $"{what} ceiling is stale, lower it to {actual} (target {target}).");
        }
    }

    private static Surface Measure()
    {
        var total = new Surface(0, 0, 0, 0);
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var s = MeasureText(file.Text);
            total = new Surface(total.Classes + s.Classes, total.Properties + s.Properties, total.EnabledFlags + s.EnabledFlags, total.Undocumented + s.Undocumented);
        }

        return total;
    }

    internal static Surface MeasureText(string text)
    {
        var lines = Regex.Split(text, "\r\n|\r|\n");
        var classes = 0;
        var properties = 0;
        var enabled = 0;
        var undocumented = 0;
        var stack = new List<(string Name, int Depth)>();
        var depth = 0;
        string? pending = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!ProgramRatchetTests.IsCommentLine(line))
            {
                classes += ProgramRatchetTests.CountOptionsInLine(line);
                enabled += ProgramRatchetTests.CountEnabledInLine(line);

                var type = TypeDeclaration().Match(line);
                if (type.Success && !line[..type.Index].Contains('='))
                {
                    pending = type.Groups[1].Value;
                }

                if (stack.Count > 0 && stack[^1].Name.EndsWith("Options", StringComparison.Ordinal) && SettableProperty().IsMatch(line))
                {
                    properties++;
                    if (!HasSummary(lines, i))
                    {
                        undocumented++;
                    }
                }

                foreach (var ch in line)
                {
                    if (ch == '{')
                    {
                        depth++;
                        if (pending is not null)
                        {
                            stack.Add((pending, depth));
                            pending = null;
                        }
                    }
                    else if (ch == '}')
                    {
                        if (stack.Count > 0 && stack[^1].Depth == depth)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        depth--;
                    }
                }
            }
        }

        return new Surface(classes, properties, enabled, undocumented);
    }

    private static bool HasSummary(string[] lines, int propertyLine)
    {
        var j = propertyLine - 1;
        while (j >= 0 && (lines[j].TrimStart().StartsWith('[') || lines[j].Trim().Length == 0))
        {
            j--;
        }

        for (var k = j; k >= 0 && lines[k].TrimStart().StartsWith("///", StringComparison.Ordinal); k--)
        {
            if (lines[k].Contains("<summary>", StringComparison.Ordinal) || lines[k].Contains("<inheritdoc", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
