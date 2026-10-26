using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-02: production source files stay at or below <see cref="MaxLines"/> lines. Files that already exceed the
/// limit are listed in Baselines/source-size-ceilings.txt ("path|ceiling") with their current line count. Ceilings
/// may only fall: a file that grows fails, a file that shrinks (or drops below the limit, or disappears) must have
/// its entry lowered or removed.
/// </summary>
public sealed class SourceSizeRatchetArchitectureTests
{
    private const int MaxLines = 1000;

    [Fact]
    public void SrcFiles_StayWithinCeiling()
    {
        var problems = Evaluate(MeasureSrc(), Baseline());

        problems.Where(p => p.StartsWith("GROWN", StringComparison.Ordinal))
            .ShouldBeEmpty("Source files over " + MaxLines + " lines must not grow; split them (ARCH-MNT-20/21/22).");
        problems.Where(p => p.StartsWith("NEW", StringComparison.Ordinal))
            .ShouldBeEmpty("New source files must stay at or below " + MaxLines + " lines.");
    }

    [Fact]
    public void Ceilings_HaveNoStaleEntries()
    {
        var problems = Evaluate(MeasureSrc(), Baseline());

        problems.Where(p => p.StartsWith("STALE", StringComparison.Ordinal))
            .ShouldBeEmpty("Ratchet ceilings may only fall; update or delete these entries in Baselines/source-size-ceilings.txt.");
    }

    [Fact]
    public void Scanner_DetectsOversizedSample()
    {
        var sample = string.Concat(Enumerable.Repeat("int x;\r\n", MaxLines + 1));
        RepositoryScan.CountLines(sample).ShouldBe(MaxLines + 1);
        RepositoryScan.CountLines(sample.Replace("\r\n", "\n")).ShouldBe(MaxLines + 1);

        var measured = new Dictionary<string, int>(StringComparer.Ordinal) { ["src/X/Big.cs"] = MaxLines + 1 };
        Evaluate(measured, new Dictionary<string, int>()).ShouldContain(p => p.StartsWith("NEW src/X/Big.cs"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines }).ShouldContain(p => p.StartsWith("GROWN"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines + 1 }).ShouldBeEmpty();
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Big.cs"] = MaxLines + 50 }).ShouldContain(p => p.StartsWith("STALE"));
        Evaluate(new Dictionary<string, int>(), new Dictionary<string, int> { ["src/X/Gone.cs"] = MaxLines + 1 }).ShouldContain(p => p.StartsWith("STALE"));
    }

    private static List<string> Evaluate(IReadOnlyDictionary<string, int> measured, IReadOnlyDictionary<string, int> baseline)
    {
        var problems = new List<string>();
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
                problems.Add($"STALE {path}: {lines} lines < ceiling {ceiling}; lower the ceiling");
            }
        }

        foreach (var (path, ceiling) in baseline.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!measured.TryGetValue(path, out var lines))
            {
                problems.Add($"STALE {path}: file no longer exists; remove the entry");
            }
            else if (lines <= MaxLines)
            {
                problems.Add($"STALE {path}: now {lines} lines (within limit); remove the entry");
            }
        }

        return problems;
    }

    private static Dictionary<string, int> MeasureSrc() =>
        RepositoryScan.ProductionFiles.ToDictionary(f => f.RelativePath, f => RepositoryScan.CountLines(f.Text), StringComparer.Ordinal);

    private static Dictionary<string, int> Baseline() =>
        RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("source-size-ceilings.txt"));
}
