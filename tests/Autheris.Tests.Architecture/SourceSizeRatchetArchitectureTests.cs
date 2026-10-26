using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-02: production source files stay at or below <see cref="MaxLines"/> lines. Files that already exceed the
/// limit are listed in Baselines/source-size-ceilings.txt ("path|ceiling") with their current line count. Ceilings
/// may only fall: a file that grows fails, a file that shrinks (or drops below the limit, or disappears) must have
/// its entry lowered or removed.
/// </summary>
public sealed class SourceSizeRatchetArchitectureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int MaxLines = 1000;

    [Fact]
    public void SrcFiles_StayWithinCeiling()
    {
        var (problems, hints) = Evaluate(MeasureSrc(), Baseline());

        foreach (var hint in hints)
        {
            output.WriteLine("HINT (not a failure): " + hint);
        }

        problems.ShouldBeEmpty("Source files over " + MaxLines + " lines must not appear or grow; split them (ARCH-MNT-20/21/22).");
    }

    [Fact]
    public void Scanner_DetectsOversizedSample()
    {
        var sample = string.Concat(Enumerable.Repeat("int x;\r\n", MaxLines + 1));
        RepositoryScan.CountLines(sample).ShouldBe(MaxLines + 1);
        RepositoryScan.CountLines(sample.Replace("\r\n", "\n")).ShouldBe(MaxLines + 1);

        var measured = new Dictionary<string, int>(StringComparer.Ordinal) { ["src/X/Big.cs"] = MaxLines + 1 };
        Evaluate(measured, new Dictionary<string, int>()).Problems.ShouldContain(p => p.StartsWith("NEW src/X/Big.cs"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines }).Problems.ShouldContain(p => p.StartsWith("GROWN"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines + 1 }).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Scanner_ToleratesShrinkAndDeletion()
    {
        var measured = new Dictionary<string, int>(StringComparer.Ordinal) { ["src/X/Big.cs"] = MaxLines + 1 };

        var shrunk = Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines + 50 });
        shrunk.Problems.ShouldBeEmpty();
        shrunk.Hints.ShouldNotBeEmpty();

        var gone = Evaluate(new Dictionary<string, int>(), new Dictionary<string, int> { ["src/X/Gone.cs"] = MaxLines + 1 });
        gone.Problems.ShouldBeEmpty();
        gone.Hints.ShouldNotBeEmpty();

        var small = Evaluate(new Dictionary<string, int> { ["src/X/Big.cs"] = 10 }, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines + 1 });
        small.Problems.ShouldBeEmpty();
        small.Hints.ShouldNotBeEmpty();
    }

    private static (List<string> Problems, List<string> Hints) Evaluate(IReadOnlyDictionary<string, int> measured, IReadOnlyDictionary<string, int> baseline)
    {
        var problems = new List<string>();
        var hints = new List<string>();
        foreach (var (path, lines) in measured.Where(m => m.Value > MaxLines).OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!baseline.TryGetValue(path, out var ceiling))
            {
                problems.Add($"NEW {path}: {lines} lines (limit {MaxLines})");
            }
            else if (lines > ceiling)
            {
                problems.Add($"GROWN {path}: {lines} lines > ceiling {ceiling}");
            }
            else if (lines < ceiling)
            {
                hints.Add($"{path}: {lines} lines < ceiling {ceiling}; lower the ceiling");
            }
        }

        foreach (var (path, ceiling) in baseline.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!measured.TryGetValue(path, out var lines))
            {
                hints.Add($"{path}: file no longer exists; remove the entry");
            }
            else if (lines <= MaxLines)
            {
                hints.Add($"{path}: now {lines} lines (within limit); remove the entry");
            }
        }

        return (problems, hints);
    }

    private static Dictionary<string, int> MeasureSrc() =>
        RepositoryScan.ProductionFiles.ToDictionary(f => f.RelativePath, f => RepositoryScan.CountLines(f.Text), StringComparer.Ordinal);

    private static Dictionary<string, int> Baseline() =>
        RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("source-size-ceilings.txt"));
}
