using System.Text;
using System.Text.Json;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>REQ-SIM-08: appends JSONL to <c>$AUTHERIS_SIM_REPORT_DIR/&lt;suite&gt;-results.jsonl</c>, one line per error class (v1 schema).</summary>
public sealed class SimResults
{
    public const string ReportDirEnvVar = "AUTHERIS_SIM_REPORT_DIR";
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, (FindingKind Kind, int Count, string FirstId, string Repro)> _lines = new(StringComparer.Ordinal);

    public static string ReportDir() =>
        Environment.GetEnvironmentVariable(ReportDirEnvVar) is { Length: > 0 } dir
            ? dir
            : throw new InvalidOperationException($"{ReportDirEnvVar} is not set.");

    public int Count => _lines.Count;

    public void Add(SimFinding finding)
    {
        _lines[finding.ErrorClass] = _lines.TryGetValue(finding.ErrorClass, out var cur)
            ? (cur.Kind, cur.Count + 1, cur.FirstId, cur.Repro)
            : (finding.Kind, 1, finding.Id, IssueDraft.Redact(finding.Repro));
    }

    /// <summary>Appends one line per error class; returns the file path.</summary>
    public string Write(string dir, string suite)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, suite + "-results.jsonl");
        var sb = new StringBuilder();
        foreach (var (errorClass, v) in _lines.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(JsonSerializer.Serialize(new
            {
                v = SchemaVersion,
                suite,
                kind = v.Kind.ToString(),
                errorClass,
                count = v.Count,
                firstId = v.FirstId,
                repro = v.Repro,
            })).Append('\n');
        }

        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
