using System.Security.Cryptography;
using System.Text;
using Autheris.Tests.Fuzz.Target;

namespace Autheris.Tests.Fuzz.Replay;

public enum ReplayStatus
{
    Accepted,
    Crash,
    Hang,

    /// <summary>Not run: oversized, symlink, missing or unreadable (fail-closed, SEC-SIM-20).</summary>
    Rejected,

    /// <summary>An AFL finding file whose replay was accepted (not reproducible); it still fails the job unless quarantined.</summary>
    NotReproduced,
}

public sealed record ReplayEntry(string Path, string Sha256, ReplayStatus Status, string? ExceptionType, string Detail, bool Quarantined)
{
    public bool IsFailure => Status != ReplayStatus.Accepted && !Quarantined;
}

public sealed record ReplayReport(IReadOnlyList<ReplayEntry> Entries)
{
    public IReadOnlyList<ReplayEntry> Failures => [.. Entries.Where(e => e.IsFailure)];

    public int ExitCode => Failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// Replays inputs in-process (no AFL needed). Reads only from the given paths, rejects symlinks and inputs above 64 KB, runs each input on a
/// dedicated thread with a 2 s <c>Join</c> and returns inputs in ordinal path order.
/// </summary>
public static class ReplayRunner
{
    private static readonly string[] IgnoredNames = [".gitkeep", "README.txt", "README.md", "quarantine.json"];

    /// <param name="paths">Files or directories (directories are read recursively).</param>
    /// <param name="quarantined">sha256 values whose crash or hang does not fail the run (still executed).</param>
    /// <param name="presenceIsFinding">AFL crash and hang directories: every file is a finding even when its replay is accepted.</param>
    /// <param name="target">Test seam; the default is the real fuzz target.</param>
    public static ReplayReport Run(IEnumerable<string> paths, IReadOnlySet<string>? quarantined = null, bool presenceIsFinding = false, Action<byte[]>? target = null)
    {
        quarantined ??= new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ReplayEntry>();
        foreach (var path in paths)
        {
            foreach (var item in Expand(path))
            {
                entries.Add(item.Reject is not null
                    ? new ReplayEntry(item.Path, string.Empty, ReplayStatus.Rejected, null, item.Reject, false)
                    : RunFile(item.Path, quarantined, presenceIsFinding, target));
            }
        }

        return new ReplayReport(entries);
    }

    private static ReplayEntry RunFile(string path, IReadOnlySet<string> quarantined, bool presenceIsFinding, Action<byte[]>? target)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = FuzzTarget.ReadCapped(stream);
            if (stream.ReadByte() >= 0)
            {
                return new ReplayEntry(path, string.Empty, ReplayStatus.Rejected, null, $"input larger than {FuzzTarget.MaxInputBytes} bytes", false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReplayEntry(path, string.Empty, ReplayStatus.Rejected, null, "unreadable: " + ex.GetType().Name, false);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var result = FuzzRunner.RunOne(bytes, target);
        var status = result.Outcome switch
        {
            FuzzOutcome.Crash => ReplayStatus.Crash,
            FuzzOutcome.Hang => ReplayStatus.Hang,
            _ => presenceIsFinding ? ReplayStatus.NotReproduced : ReplayStatus.Accepted,
        };
        var isQuarantined = status != ReplayStatus.Accepted && quarantined.Contains(sha);
        return new ReplayEntry(path, sha, status, result.ExceptionType, $"{result.Elapsed.TotalMilliseconds:F0} ms{ThrownAt(result.Exception)}", isQuarantined);
    }

    private static string ThrownAt(Exception? exception) =>
        exception is null
            ? string.Empty
            : " " + (exception.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("TrinoSqlEngine", StringComparison.Ordinal))?.Trim().Split(" in ")[0] ?? "at " + exception.TargetSite?.Name);

    private readonly record struct Item(string Path, string? Reject);

    private static IEnumerable<Item> Expand(string path)
    {
        if (IsLink(path))
        {
            yield return new Item(path, "symlink rejected");
            yield break;
        }

        if (File.Exists(path))
        {
            yield return new Item(path, null);
            yield break;
        }

        if (!Directory.Exists(path))
        {
            yield return new Item(path, "path does not exist");
            yield break;
        }

        var files = new List<string>();
        var rejects = new List<Item>();
        Walk(path, files, rejects);
        foreach (var reject in rejects)
        {
            yield return reject;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return new Item(file, null);
        }
    }

    private static void Walk(string dir, List<string> files, List<Item> rejects)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(entry);
            if (IsLink(entry))
            {
                rejects.Add(new Item(entry, "symlink rejected"));
            }
            else if (Directory.Exists(entry))
            {
                Walk(entry, files, rejects);
            }
            else if (!IgnoredNames.Contains(name, StringComparer.Ordinal) && !name.EndsWith(".dict", StringComparison.Ordinal))
            {
                files.Add(entry);
            }
        }
    }

    private static bool IsLink(string path)
    {
        return new FileInfo(path).LinkTarget is not null;
    }

    /// <summary>Markdown draft for one finding (never written for an accepted input). The input is capped at 4 KB.</summary>
    public static string RenderDraft(ReplayEntry entry, byte[] input)
    {
        var text = Encoding.UTF8.GetString(input, 0, Math.Min(input.Length, 4096)).Replace("\r", string.Empty, StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("# Parser fuzz finding ").Append(entry.Sha256.AsSpan(0, 12)).Append("\n\n");
        sb.Append("Outcome: ").Append(entry.Status).Append('\n');
        sb.Append("Exception: ").Append(entry.ExceptionType ?? "none").Append('\n');
        sb.Append("Input sha256: ").Append(entry.Sha256).Append("\n\n");
        sb.Append("## Minimal input\n\n```sql\n").Append(text).Append("\n```\n\n");
        sb.Append("## Repro\n\n```\ndotnet run --project tests/Autheris.Tests.Fuzz -- --replay <file with the input above>\n```\n");
        return sb.ToString();
    }

    /// <summary>Writes <c>FUZZ-&lt;sha12&gt;.md</c> per failure (readable inputs only) into <paramref name="dir"/>.</summary>
    public static void WriteDrafts(ReplayReport report, string dir)
    {
        foreach (var entry in report.Failures.Where(f => f.Sha256.Length == 64 && f.Status is ReplayStatus.Crash or ReplayStatus.Hang))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "FUZZ-" + entry.Sha256[..12] + ".md"), RenderDraft(entry, File.ReadAllBytes(entry.Path)), new UTF8Encoding(false));
        }
    }
}
