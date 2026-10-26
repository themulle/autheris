using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-23: fixed sleeps in tests (<c>Task.Delay(</c>, <c>Thread.Sleep(</c>) may only fall (per-file counts in
/// Baselines/test-sleep-counts.txt; only new or grown usage fails, decreases yield a hint). Synchronise with
/// <c>WaitHelper.WaitUntil</c>, a <c>TaskCompletionSource</c> or a <c>FakeTimeProvider</c> instead.
/// This project is excluded from the scan.
/// </summary>
public sealed partial class TestSleepRatchetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string SelfPrefix = "tests/Autheris.Tests.Architecture/";

    [GeneratedRegex(@"\b(Task\.Delay|Thread\.Sleep)\s*\(")]
    private static partial Regex Sleep();

    [Fact]
    public void TestSleepUsage_DoesNotGrow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("test-sleep-counts.txt"));
        var files = RepositoryScan.EnumerateFiles("tests", "*.cs")
            .Select(RepositoryScan.Normalize)
            .Select(p => (Path: p, Text: RepositoryScan.ReadText(p)));
        var measured = Measure(files);

        var (problems, hints) = Evaluate(measured, baseline);

        foreach (var hint in hints)
        {
            output.WriteLine("HINT (not a failure): " + hint);
        }

        problems.ShouldBeEmpty("Fixed sleeps in tests must not grow; use WaitHelper.WaitUntil, a TaskCompletionSource or FakeTimeProvider.");
    }

    [Fact]
    public void Scanner_DetectsPlantedUsage()
    {
        var sample = "// await Task.Delay(1) in a comment\r\nawait Task.Delay(5);\nThread.Sleep (10);\nvar x = WaitHelper.WaitUntil(f, t);\n";

        var measured = Measure([("tests/X/Sample.cs", sample), (SelfPrefix + "Own.cs", sample)]);
        measured.Keys.ShouldBe(["tests/X/Sample.cs"]);
        measured["tests/X/Sample.cs"].ShouldBe(2);
        Evaluate(measured, new Dictionary<string, int>()).Problems.ShouldContain(p => p.StartsWith("NEW"));
        Evaluate(measured, new Dictionary<string, int> { ["tests/X/Sample.cs"] = 1 }).Problems.ShouldContain(p => p.StartsWith("GROWN"));
        Evaluate(measured, new Dictionary<string, int> { ["tests/X/Sample.cs"] = 2 }).Problems.ShouldBeEmpty();
        Evaluate(measured, new Dictionary<string, int> { ["tests/X/Sample.cs"] = 5 }).Problems.ShouldBeEmpty();
        Evaluate(measured, new Dictionary<string, int> { ["tests/X/Sample.cs"] = 5 }).Hints.ShouldNotBeEmpty();
        Evaluate(new Dictionary<string, int>(), new Dictionary<string, int> { ["tests/X/Sample.cs"] = 2 }).Problems.ShouldBeEmpty();
    }

    private static Dictionary<string, int> Measure(IEnumerable<(string Path, string Text)> files)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            if (path.StartsWith(SelfPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var count = RepositoryScan.CodeLines(text).Sum(l => Sleep().Count(l));
            if (count > 0)
            {
                result[path] = count;
            }
        }

        return result;
    }

    private static (List<string> Problems, List<string> Hints) Evaluate(IReadOnlyDictionary<string, int> measured, IReadOnlyDictionary<string, int> baseline)
    {
        var problems = new List<string>();
        var hints = new List<string>();
        foreach (var (path, count) in measured.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!baseline.TryGetValue(path, out var ceiling))
            {
                problems.Add($"NEW {path}: {count}");
            }
            else if (count > ceiling)
            {
                problems.Add($"GROWN {path}: {count} > {ceiling}");
            }
            else if (count < ceiling)
            {
                hints.Add($"{path}: {count} < {ceiling}; lower the ceiling");
            }
        }

        foreach (var (path, _) in baseline.Where(b => !measured.ContainsKey(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            hints.Add($"{path}: no longer sleeps; remove the entry");
        }

        return (problems, hints);
    }
}
