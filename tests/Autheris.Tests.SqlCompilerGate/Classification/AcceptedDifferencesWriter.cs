namespace Autheris.Tests.SqlCompilerGate.Classification;

using System.Text.Json;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>
/// Proposes the <c>accepted-differences.json</c> entries for the current results. A proposal is only a triage aid: it writes
/// <c>pending</c> entries (no issue, no reviewer) that the security reviewers must review and approve. Approved entries are kept as
/// they are. Never written by a normal run: only when <c>SQLGATE_UPDATE_ACCEPTED=&lt;path&gt;</c> is set.
/// </summary>
public static class AcceptedDifferencesWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Merge(string path, GateDialect dialect, IEnumerable<StatementResult> results)
    {
        var current = File.Exists(path) ? AcceptedDifferences.Parse(File.ReadAllText(path)) : AcceptedDifferences.Empty;
        var kept = current.Entries.Where(e => e.Approval == "approved" || !e.Dialects.Contains(dialect.ToString())).ToList();

        // pending entries of other dialects stay, pending entries of this dialect are recomputed
        var approved = AcceptedDifferences.Parse(JsonSerializer.Serialize(new { schemaVersion = 1, entries = kept.Where(e => e.Approval == "approved") }, Options));
        var proposed = results
            .Where(r => r.Classification.Class is DifferenceClass.AstStricter or DifferenceClass.AstError)
            .Where(r => !approved.Accepts(r.Statement.Id, dialect, r.Classification.Class, r.Classification.Reason))
            .GroupBy(r => (Class: r.Classification.Class.ToWireName(), r.Classification.Reason))
            .OrderBy(g => g.Key.Class, StringComparer.Ordinal).ThenBy(g => g.Key.Reason, StringComparer.Ordinal)
            .Select(g => new AcceptedEntry(
                [dialect.ToString()],
                g.Key.Class,
                g.Key.Reason,
                [.. g.Select(r => r.Statement.Id).Distinct().Order(StringComparer.Ordinal)],
                $"Triage proposal of the gate run: {g.Key.Class} with reason {g.Key.Reason}. Needs security review before approval.",
                null,
                null,
                "pending"));

        var entries = kept.Concat(proposed)
            .OrderBy(e => e.Dialects[0], StringComparer.Ordinal).ThenBy(e => e.Class, StringComparer.Ordinal).ThenBy(e => e.Reason, StringComparer.Ordinal)
            .Select(e => new
            {
                dialects = e.Dialects,
                @class = e.Class,
                reason = e.Reason,
                statements = e.Statements,
                justification = e.Justification,
                issue = e.Issue,
                reviewer = e.Reviewer,
                approval = e.Approval
            });
        var legacyDefects = current.LegacyDefects.Select(l => new
        {
            dialects = l.Dialects,
            statements = l.Statements,
            defect = l.Defect,
            justification = l.Justification,
            issue = l.Issue,
            reviewer = l.Reviewer,
            approval = l.Approval
        });
        // legacyDefects are human decisions: a proposal run keeps them untouched
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, entries, legacyDefects }, Options) + "\n");
    }
}
