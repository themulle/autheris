namespace Autheris.Tests.SqlCompilerGate.Classification;

using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>
/// <c>accepted-differences.json</c> (plan 9.3 G4, 16.5 M-6). It lists the <c>ast-stricter</c> and <c>ast-error</c> differences that
/// security reviewers accepted. Its schema (<c>accepted-differences.schema.json</c>) allows no other class, so an
/// <c>ast-looser-*</c> entry fails validation and can never be accepted. The file is owned by the security reviewers (CODEOWNERS).
/// </summary>
public sealed record AcceptedEntry(
    IReadOnlyList<string> Dialects,
    string Class,
    string Reason,
    IReadOnlyList<string> Statements,
    string Justification,
    string? Issue,
    string? Reviewer,
    string Approval);

public sealed class AcceptedDifferences
{
    /// <summary>The only classes that may be accepted. This must equal the <c>class</c> enum of the schema file (checked by a test).</summary>
    public static readonly IReadOnlyList<string> AcceptableClasses = ["ast-stricter", "ast-error"];

    private static readonly string[] Approvals = ["pending", "approved"];
    private static readonly Regex IssuePattern = new(@"^(https://\S+|[A-Z][A-Z0-9]+-[0-9]+|#[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ReviewerPattern = new(@"^@[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly HashSet<(string Statement, string Dialect, string Class, string Reason)> _index = [];

    public IReadOnlyList<AcceptedEntry> Entries { get; }

    private AcceptedDifferences(IReadOnlyList<AcceptedEntry> entries)
    {
        Entries = entries;
        foreach (var e in entries)
        {
            foreach (var s in e.Statements)
            {
                foreach (var d in e.Dialects) _index.Add((s, d, e.Class, e.Reason));
            }
        }
    }

    public static AcceptedDifferences Empty { get; } = new([]);

    public bool Accepts(string statementId, GateDialect dialect, DifferenceClass cls, string reason) =>
        _index.Contains((statementId, dialect.ToString(), cls.ToWireName(), reason));

    public bool HasPendingEntries => Entries.Any(e => e.Approval == "pending");

    /// <summary>Parses and validates <paramref name="json"/>. Throws <see cref="AcceptedDifferencesSchemaException"/> listing every violation.</summary>
    public static AcceptedDifferences Parse(string json)
    {
        var errors = new List<string>();
        var entries = new List<AcceptedEntry>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AcceptedDifferencesSchemaException([$"not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AcceptedDifferencesSchemaException(["the root must be an object"]);
            }

            RejectUnknown(root, ["schemaVersion", "entries"], "$", errors);
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                errors.Add("$.schemaVersion must be 1");
            }

            if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                errors.Add("$.entries must be an array");
            }
            else
            {
                int index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    var entry = ParseEntry(item, $"$.entries[{index++}]", errors);
                    if (entry is not null) entries.Add(entry);
                }
            }
        }

        if (errors.Count > 0) throw new AcceptedDifferencesSchemaException(errors);
        return new AcceptedDifferences(entries);
    }

    private static AcceptedEntry? ParseEntry(JsonElement item, string path, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object");
            return null;
        }

        int before = errors.Count;
        RejectUnknown(item, ["dialects", "class", "reason", "statements", "justification", "issue", "reviewer", "approval"], path, errors);

        string cls = RequiredString(item, "class", path, errors);
        if (cls.Length > 0 && !AcceptableClasses.Contains(cls, StringComparer.Ordinal))
        {
            errors.Add($"{path}.class '{cls}' is not acceptable: only {string.Join(" and ", AcceptableClasses)} may be accepted");
        }

        string reason = RequiredString(item, "reason", path, errors);
        string justification = RequiredString(item, "justification", path, errors);
        if (justification.Length > 0 && justification.Trim().Length < 20) errors.Add($"{path}.justification must be at least 20 characters");
        string approval = RequiredString(item, "approval", path, errors);
        if (approval.Length > 0 && !Approvals.Contains(approval, StringComparer.Ordinal)) errors.Add($"{path}.approval must be pending or approved");

        var dialects = StringArray(item, "dialects", path, errors);
        foreach (var d in dialects)
        {
            if (!Enum.TryParse<GateDialect>(d, out _)) errors.Add($"{path}.dialects contains unknown dialect '{d}'");
        }

        var statements = StringArray(item, "statements", path, errors);
        if (statements.Any(s => !Regex.IsMatch(s, "^[0-9a-f]{12}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
        {
            errors.Add($"{path}.statements must hold 12-digit hexadecimal corpus ids");
        }

        string? issue = OptionalString(item, "issue", path, errors);
        string? reviewer = OptionalString(item, "reviewer", path, errors);
        if (issue is not null && !IssuePattern.IsMatch(issue)) errors.Add($"{path}.issue must be an issue link or key");
        if (reviewer is not null && !ReviewerPattern.IsMatch(reviewer)) errors.Add($"{path}.reviewer must be a reviewer handle (@name)");
        if (approval == "approved" && (issue is null || reviewer is null))
        {
            errors.Add($"{path} is approved and so needs an issue and a reviewer");
        }

        return errors.Count == before ? new AcceptedEntry(dialects, cls, reason, statements, justification, issue, reviewer, approval) : null;
    }

    private static void RejectUnknown(JsonElement obj, string[] known, string path, List<string> errors)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.Ordinal)) errors.Add($"{path}.{p.Name} is not allowed");
        }
    }

    private static string RequiredString(JsonElement obj, string name, string path, List<string> errors)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
        {
            return v.GetString()!;
        }

        errors.Add($"{path}.{name} is required");
        return string.Empty;
    }

    private static string? OptionalString(JsonElement obj, string name, string path, List<string> errors)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        errors.Add($"{path}.{name} must be a string or null");
        return null;
    }

    private static List<string> StringArray(JsonElement obj, string name, string path, List<string> errors)
    {
        var result = new List<string>();
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
        {
            errors.Add($"{path}.{name} must be a non-empty array");
            return result;
        }

        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String) result.Add(e.GetString()!);
            else errors.Add($"{path}.{name} must hold strings");
        }

        return result;
    }
}

public sealed class AcceptedDifferencesSchemaException(IReadOnlyList<string> errors)
    : Exception("accepted-differences.json is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
