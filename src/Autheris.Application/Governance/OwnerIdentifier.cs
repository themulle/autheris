namespace Autheris.Application.Governance;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Comparison of owner identifiers (I393B-SC-05): trimmed, Unicode-normalized (FormC) and case-insensitive, independent of the database
/// collation. The same rules apply to self-assignment, collision and existing-row lookup.
/// </summary>
public static class OwnerIdentifier
{
    public static string Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Trim().Normalize(NormalizationForm.FormC);

    public static bool AreSame(string? left, string? right)
    {
        var l = Normalize(left);
        return l.Length > 0 && string.Equals(l, Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The identifier itself plus its part after the last <c>:</c> (ITSM / provider prefixes), as <c>ConsentApprovalPolicy.IdentifierCandidates</c> does.</summary>
    public static IEnumerable<string> Candidates(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;
        var idx = normalized.LastIndexOf(':');
        if (idx >= 0 && idx < normalized.Length - 1)
        {
            yield return normalized[(idx + 1)..].Trim();
        }
    }

    /// <summary>True when any normalized candidate of <paramref name="left"/> equals any candidate of <paramref name="right"/>.</summary>
    public static bool ShareCandidate(string? left, IReadOnlyCollection<string> rightCandidates)
    {
        foreach (var candidate in Candidates(left))
        {
            foreach (var other in rightCandidates)
            {
                if (string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
