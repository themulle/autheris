using System.Text.RegularExpressions;
using Shouldly;

namespace Autheris.Tests.Architecture;

/// <summary>
/// ARCH-MNT-04 (REV-03, REV-09): wall-clock reads in production code may only fall (per-file counts in
/// Baselines/datetime-now-counts.txt), and <c>new HttpClient(</c> is banned outside a reviewed allowlist so that
/// egress handlers (SSRF guard, resilience) are not bypassed.
/// </summary>
public sealed partial class ClockAndHttpClientRatchetTests
{
    // Files allowed to construct HttpClient directly. Anything else must use IHttpClientFactory.
    private static readonly string[] HttpClientAllowlist =
    [
        "src/Autheris.Api/Security/HealthCheckCli.cs",
        "src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs",
        "src/Autheris.Infrastructure/Telemetry/OtlpHttpClientFactory.cs"
    ];

    [GeneratedRegex(@"\bDateTime(Offset)?\.(Utc)?Now\b")]
    private static partial Regex ClockRead();

    [GeneratedRegex(@"\bnew\s+HttpClient\s*\(")]
    private static partial Regex NewHttpClient();

    [Fact]
    public void DateTimeNowUsage_DoesNotGrow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("datetime-now-counts.txt"));
        var measured = Measure(RepositoryScan.ProductionFiles.Select(f => (f.RelativePath, f.Text)));

        var problems = Evaluate(measured, baseline);

        problems.Where(p => !p.StartsWith("STALE", StringComparison.Ordinal))
            .ShouldBeEmpty("Wall-clock reads (DateTime.UtcNow/Now) must not grow; inject TimeProvider instead.");
        problems.Where(p => p.StartsWith("STALE", StringComparison.Ordinal))
            .ShouldBeEmpty("Ratchet may only fall; lower or delete these entries in Baselines/datetime-now-counts.txt.");
    }

    [Fact]
    public void NewHttpClient_IsOnlyUsedInAllowlist()
    {
        var users = RepositoryScan.ProductionFiles
            .Where(f => FindHttpClientConstruction(f.Text))
            .Select(f => f.RelativePath)
            .ToList();

        users.Except(HttpClientAllowlist, StringComparer.Ordinal)
            .ShouldBeEmpty("new HttpClient(...) is banned outside the allowlist; use IHttpClientFactory so egress handlers apply.");
        HttpClientAllowlist.Except(users, StringComparer.Ordinal)
            .ShouldBeEmpty("Stale HttpClient allowlist entries; remove them.");
    }

    [Fact]
    public void Scanner_DetectsPlantedUsage()
    {
        var sample = "// DateTime.UtcNow in a comment\r\nvar a = DateTime.UtcNow;\nvar b = DateTimeOffset.Now;\nvar c = new HttpClient();\n";

        var measured = Measure([("src/X/Sample.cs", sample)]);
        measured["src/X/Sample.cs"].ShouldBe(2);
        Evaluate(measured, new Dictionary<string, int>()).ShouldContain(p => p.StartsWith("NEW"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Sample.cs"] = 1 }).ShouldContain(p => p.StartsWith("GROWN"));
        Evaluate(measured, new Dictionary<string, int> { ["src/X/Sample.cs"] = 2 }).ShouldBeEmpty();
        FindHttpClientConstruction(sample).ShouldBeTrue();
        FindHttpClientConstruction("// new HttpClient()\nvar f = factory.CreateClient();").ShouldBeFalse();
    }

    private static bool FindHttpClientConstruction(string text) =>
        RepositoryScan.CodeLines(text).Any(l => NewHttpClient().IsMatch(l));

    private static Dictionary<string, int> Measure(IEnumerable<(string Path, string Text)> files)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            var count = RepositoryScan.CodeLines(text).Sum(l => ClockRead().Count(l));
            if (count > 0)
            {
                result[path] = count;
            }
        }

        return result;
    }

    private static List<string> Evaluate(IReadOnlyDictionary<string, int> measured, IReadOnlyDictionary<string, int> baseline)
    {
        var problems = new List<string>();
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
                problems.Add($"STALE {path}: {count} < {ceiling}");
            }
        }

        foreach (var (path, _) in baseline.Where(b => !measured.ContainsKey(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            problems.Add($"STALE {path}: no longer reads the clock");
        }

        return problems;
    }
}
