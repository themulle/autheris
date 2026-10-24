using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Fuzz.Replay;

/// <summary>
/// <c>findings/quarantine.json</c> (owner-controlled, SEC-SIM-17): <c>[{sha256, issue, reason, approvedOn}]</c>. Fail-closed: unknown keys,
/// malformed hashes, non-positive issue numbers, missing reason or date, duplicates and entries older than 90 days all throw.
/// A quarantined input still runs in replay; only its outcome stops failing the job.
/// </summary>
public static partial class FuzzQuarantine
{
    public const int MaxAgeDays = 90;

    private static readonly string[] AllowedKeys = ["sha256", "issue", "reason", "approvedOn"];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static IReadOnlySet<string> Load(string path, DateOnly today) =>
        Parse(File.ReadAllText(path), today);

    public static IReadOnlySet<string> Parse(string json, DateOnly today)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("quarantine.json must be a JSON array.");
        }

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("quarantine entry must be an object.");
            }

            if (element.EnumerateObject().Any(p => !AllowedKeys.Contains(p.Name, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("quarantine entry has unknown keys.");
            }

            var sha = Text(element, "sha256");
            if (!Sha256Pattern().IsMatch(sha))
            {
                throw new InvalidOperationException("quarantine sha256 must be 64 lowercase hex characters.");
            }

            if (!element.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Number || !issue.TryGetInt32(out var n) || n <= 0)
            {
                throw new InvalidOperationException("quarantine issue must be a positive integer.");
            }

            if (string.IsNullOrWhiteSpace(Text(element, "reason")))
            {
                throw new InvalidOperationException("quarantine reason is required.");
            }

            if (!DateOnly.TryParseExact(Text(element, "approvedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var approved))
            {
                throw new InvalidOperationException("quarantine approvedOn must be yyyy-MM-dd.");
            }

            if (approved > today || today.DayNumber - approved.DayNumber > MaxAgeDays)
            {
                throw new InvalidOperationException("quarantine entry is expired (older than 90 days) or dated in the future.");
            }

            if (!hashes.Add(sha))
            {
                throw new InvalidOperationException("quarantine sha256 must be unique.");
            }
        }

        return hashes;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : throw new InvalidOperationException($"quarantine entry requires string '{name}'.");
}
