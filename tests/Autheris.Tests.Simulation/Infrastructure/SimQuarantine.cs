using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Simulation.Infrastructure;

/// <summary>One owner-approved quarantine entry (SEC-SIM-17).</summary>
public sealed record SimQuarantineEntry(string Sha256, int Issue, string Reason, DateOnly ApprovedOn);

/// <summary>
/// <c>findings/quarantine.json</c>: <c>[{sha256, issue, reason, approvedOn}]</c>. Fail-closed: unknown keys, malformed hashes, non-positive issue
/// numbers, missing reason or date, and entries older than 90 days are all rejected. A quarantined input still runs in replay.
/// </summary>
public static partial class SimQuarantine
{
    public const int MaxAgeDays = 90;

    private static readonly string[] AllowedKeys = ["sha256", "issue", "reason", "approvedOn"];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static IReadOnlyList<SimQuarantineEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("quarantine.json must be a JSON array.");
        }

        var entries = new List<SimQuarantineEntry>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("quarantine entry must be an object.");
            }

            var unknown = element.EnumerateObject().Select(p => p.Name).Except(AllowedKeys, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"quarantine entry has unknown keys: {string.Join(",", unknown)}.");
            }

            var sha = Text(element, "sha256");
            if (!Sha256Pattern().IsMatch(sha))
            {
                throw new InvalidOperationException("quarantine sha256 must be 64 lowercase hex characters.");
            }

            if (!element.TryGetProperty("issue", out var issueEl) || issueEl.ValueKind != JsonValueKind.Number || !issueEl.TryGetInt32(out var issue) || issue <= 0)
            {
                throw new InvalidOperationException("quarantine issue must be a positive integer.");
            }

            var reason = Text(element, "reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new InvalidOperationException("quarantine reason is required.");
            }

            if (!DateOnly.TryParseExact(Text(element, "approvedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var approved))
            {
                throw new InvalidOperationException("quarantine approvedOn must be yyyy-MM-dd.");
            }

            entries.Add(new SimQuarantineEntry(sha, issue, reason, approved));
        }

        return entries;
    }

    /// <summary>Entries older than <see cref="MaxAgeDays"/> (or dated in the future) at <paramref name="today"/>.</summary>
    public static IReadOnlyList<SimQuarantineEntry> Expired(IEnumerable<SimQuarantineEntry> entries, DateOnly today) =>
        entries.Where(e => e.ApprovedOn > today || today.DayNumber - e.ApprovedOn.DayNumber > MaxAgeDays).ToList();

    public static bool IsQuarantined(IEnumerable<SimQuarantineEntry> entries, string sha256) =>
        entries.Any(e => string.Equals(e.Sha256, sha256, StringComparison.Ordinal));

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : throw new InvalidOperationException($"quarantine entry requires string '{name}'.");
}
