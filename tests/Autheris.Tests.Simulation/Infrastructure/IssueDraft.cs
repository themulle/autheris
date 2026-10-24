using System.Text;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>
/// REQ-SIM-05 / SEC-SIM-14: writes <c>&lt;id&gt;.md</c>. Canaries become <c>&lt;canary&gt;</c>, header and auth lines are dropped,
/// each cell is capped at 200 chars and 20 rows.
/// </summary>
public static partial class IssueDraft
{
    public const int MaxCellChars = 200;
    public const int MaxRows = 20;

    [GeneratedRegex(@"SIMCANARY-[A-Za-z0-9\-]+")]
    private static partial Regex Canary();

    [GeneratedRegex(@"^\s*(X-Test-[\w-]*|Authorization|Cookie|Set-Cookie|Proxy-Authorization)\s*[:=]", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveHeaderLine();

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,64}$")]
    private static partial Regex SafeId();

    public static string Render(SimFinding finding)
    {
        var sb = new StringBuilder();
        sb.Append("# Simulation finding ").Append(Cell(finding.Id)).Append('\n').Append('\n');
        sb.Append("Kind: ").Append(finding.Kind).Append('\n');
        sb.Append("Error class: ").Append(Cell(finding.ErrorClass)).Append('\n').Append('\n');
        Section(sb, "Minimal input", finding.MinimalInput);
        Section(sb, "Expected", finding.Expected);
        Section(sb, "Actual", finding.Actual);
        Section(sb, "Repro", finding.Repro);
        return sb.ToString();
    }

    /// <summary>Writes <c>&lt;id&gt;.md</c> into <paramref name="dir"/> and returns the path. The id must be a safe file stem.</summary>
    public static string Write(string dir, SimFinding finding, string? id = null)
    {
        var stem = id ?? finding.Id;
        if (!SafeId().IsMatch(stem))
        {
            throw new ArgumentException("Draft id must match [A-Za-z0-9-]{1,64}.", nameof(id));
        }

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, stem + ".md");
        File.WriteAllText(path, Render(finding), new UTF8Encoding(false));
        return path;
    }

    public static string Redact(string text) => Canary().Replace(text, "<canary>");

    private static void Section(StringBuilder sb, string title, string body)
    {
        sb.Append("## ").Append(title).Append('\n').Append('\n').Append("```\n");
        var rows = Redact(body).Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !SensitiveHeaderLine().IsMatch(l))
            .Take(MaxRows);
        foreach (var row in rows)
        {
            sb.Append(Cell(row)).Append('\n');
        }

        sb.Append("```\n\n");
    }

    private static string Cell(string value)
    {
        var redacted = Redact(value).Replace("```", "'''", StringComparison.Ordinal);
        return redacted.Length <= MaxCellChars ? redacted : redacted[..MaxCellChars];
    }
}
