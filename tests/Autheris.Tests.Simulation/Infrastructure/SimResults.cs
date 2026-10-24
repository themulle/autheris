using System.Text;
using System.Text.Json;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>REQ-SIM-08: appends JSONL to <c>$AUTHERIS_SIM_REPORT_DIR/&lt;suite&gt;-results.jsonl</c>, one line per error class (v1 schema).</summary>
public sealed class SimResults
{
    public const string ReportDirEnvVar = "AUTHERIS_SIM_REPORT_DIR";
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, (FindingKind Kind, int Count, string FirstId, string Repro, string? Disposition)> _lines = new(StringComparer.Ordinal);
    private readonly List<(string Kind, string ErrorClass, int Count, string Repro)> _notes = [];

    public static string ReportDir() =>
        Environment.GetEnvironmentVariable(ReportDirEnvVar) is { Length: > 0 } dir
            ? dir
            : throw new InvalidOperationException($"{ReportDirEnvVar} is not set.");

    public int Count => _lines.Count;

    /// <summary>Adds one occurrence. <paramref name="disposition"/> (for example <c>KnownGap</c>) is written only when set.</summary>
    public void Add(SimFinding finding, string? disposition = null)
    {
        _lines[finding.ErrorClass] = _lines.TryGetValue(finding.ErrorClass, out var cur)
            ? (cur.Kind, cur.Count + 1, cur.FirstId, cur.Repro, cur.Disposition)
            : (finding.Kind, 1, finding.Id, IssueDraft.Redact(finding.Repro), disposition);
    }

    /// <summary>A run statistic line (kind such as <c>Executed</c> or <c>Equal</c>, not a finding kind). Written after the finding lines.</summary>
    public void AddNote(string kind, string errorClass, int count, string repro) =>
        _notes.Add((kind, errorClass, count, IssueDraft.Redact(repro)));

    /// <summary>Appends one line per error class; returns the file path.</summary>
    public string Write(string dir, string suite)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, suite + "-results.jsonl");
        var sb = new StringBuilder();
        foreach (var (errorClass, v) in _lines.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var line = new Dictionary<string, object>
            {
                ["v"] = SchemaVersion,
                ["suite"] = suite,
                ["kind"] = v.Kind.ToString(),
                ["errorClass"] = errorClass,
                ["count"] = v.Count,
                ["firstId"] = v.FirstId,
                ["repro"] = v.Repro,
            };
            if (v.Disposition is not null)
            {
                line["disposition"] = v.Disposition;
            }

            sb.Append(JsonSerializer.Serialize(line)).Append('\n');
        }

        foreach (var (kind, errorClass, count, repro) in _notes)
        {
            sb.Append(JsonSerializer.Serialize(new { v = SchemaVersion, suite, kind, errorClass, count, firstId = string.Empty, repro })).Append('\n');
        }

        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
