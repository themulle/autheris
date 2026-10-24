// REQ-SIMTEST P3: SharpFuzz harness for TrinoSqlEngine (simulation testing only; never part of Autheris.sln, SEC-SIM-21).
using System.Globalization;
using Autheris.Tests.Fuzz.Replay;
using Autheris.Tests.Fuzz.Target;
using SharpFuzz;

const string Usage = """
    Autheris.Tests.Fuzz: SharpFuzz harness for TrinoSqlEngine (simulation testing only).

    Modes:
      --afl                         AFL++ out-of-process target (stdin, instrumented build only, see scripts/sim/run-parser-fuzz.sh)
      --replay <file|dir>...        replay inputs in-process, exit 1 on a crash, hang or rejected input
                                    [--quarantine <file>] [--drafts <dir>]
      --afl-findings <dir>...       replay AFL crashes/hangs; every file is a finding unless quarantined (empty dirs are fine)
                                    [--quarantine <file>] [--drafts <dir>]
      --mutate <count> <seed> <dir> seeded in-process mutation smoke over the seeds of <dir> (local stand-in for AFL) [--out <dir>]
      --export-seeds <dir>          export the seed corpus derived from our own dql.sql [--dql <file>]
      --export-dict <file>          export the AFL dictionary

    Local run (no AFL needed):
      dotnet run --project tests/Autheris.Tests.Fuzz -- --replay tests/Autheris.Tests.Fuzz/corpus tests/Autheris.Tests.Fuzz/findings
    """;

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    switch (args[0])
    {
        case "--afl" when args.Length == 1:
            Fuzzer.OutOfProcess.Run(FuzzTarget.Run);
            return 0;
        case "--replay" or "--afl-findings":
            return Replay(args);
        case "--mutate" when args.Length is 4 or 6:
            return Mutate(args);
        case "--export-seeds" when args.Length is 2 or 4:
            SeedCorpus.Export(Option(args, "--dql") ?? Path.Combine(RepoRoot(), SeedCorpus.DqlRelativePath), args[1]);
            return 0;
        case "--export-dict" when args.Length == 2:
            File.WriteAllText(args[1], SeedCorpus.BuildDictionary(), new System.Text.UTF8Encoding(false));
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("fuzz harness error: " + ex.Message);
    return 2;
}

static int Replay(string[] args)
{
    var afl = args[0] == "--afl-findings";
    var paths = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] is "--quarantine" or "--drafts")
        {
            i++;
        }
        else
        {
            paths.Add(args[i]);
        }
    }

    var quarantinePath = Option(args, "--quarantine") ?? Path.Combine(RepoRoot(), "tests", "Autheris.Tests.Fuzz", "findings", "quarantine.json");
    var quarantine = FuzzQuarantine.Load(quarantinePath, DateOnly.FromDateTime(DateTime.UtcNow));
    var report = ReplayRunner.Run(paths, quarantine, presenceIsFinding: afl);
    foreach (var entry in report.Entries.Where(e => e.Status != ReplayStatus.Accepted || e.Quarantined))
    {
        Console.WriteLine($"{entry.Status}{(entry.Quarantined ? " (quarantined)" : string.Empty)} {entry.Path} sha256={entry.Sha256} {entry.ExceptionType} {entry.Detail}");
    }

    Console.WriteLine($"replayed {report.Entries.Count} inputs, {report.Failures.Count} failures");
    if (Option(args, "--drafts") is { } drafts)
    {
        ReplayRunner.WriteDrafts(report, drafts);
    }

    if (report.Entries.Count == 0 && !afl)
    {
        Console.Error.WriteLine("no inputs replayed (vacuous run is a failure)");
        return 1;
    }

    return report.ExitCode;
}

static int Mutate(string[] args)
{
    var count = int.Parse(args[1], CultureInfo.InvariantCulture);
    var seed = int.Parse(args[2], CultureInfo.InvariantCulture);
    var seeds = Directory.EnumerateFiles(args[3]).Where(f => !f.EndsWith(".dict", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    var report = MutationSmoke.Run(seeds, count, seed);
    Console.WriteLine($"mutation smoke: executed {report.Executed}, accepted {report.Accepted}, findings {report.Findings.Count}");
    var outDir = Option(args, "--out");
    foreach (var group in report.Findings.GroupBy(f => $"{f.Result.Outcome}:{f.Result.ExceptionType}"))
    {
        var first = group.First();
        Console.WriteLine($"  {group.Key} x{group.Count()} first input #{first.Index} ({first.Input.Length} bytes): {System.Text.Encoding.UTF8.GetString(first.Input, 0, Math.Min(first.Input.Length, 200)).ReplaceLineEndings(" ")}");
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            foreach (var finding in group.Take(3))
            {
                File.WriteAllBytes(Path.Combine(outDir, $"finding-{finding.Index}.bin"), finding.Input);
            }
        }
    }

    return report.Findings.Count == 0 ? 0 : 1;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string RepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tests", "Autheris.Tests.Fuzz", "Autheris.Tests.Fuzz.csproj")))
            {
                return dir.FullName;
            }
        }
    }

    throw new InvalidOperationException("repository root not found (run from the repository root)");
}
