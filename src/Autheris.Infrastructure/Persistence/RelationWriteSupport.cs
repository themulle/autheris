using System.Text;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Engine-neutral parts of <c>CreateRelationAsync</c> (issue #151): rule check, column canonicalisation, change detection.</summary>
internal static class RelationWriteSupport
{
    private const int MaxIdentifierInMessage = 128;

    /// <summary>Runs <see cref="RelationKeyRules"/> before any connection is opened (SEC-REL151-03).</summary>
    public static void ValidateKeysOrThrow(TableRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (RelationKeyRules.Validate(relation.JoinKeysParent, relation.JoinKeysChild) is { } violation)
        {
            throw new InvalidRelationException(violation, $"Relation '{Safe(relation.RelationName)}' has invalid join keys: {violation}.");
        }
    }

    public static InvalidRelationException TableNotResolved(string relationName, TableIdentifier table, int matches) => new(
        null,
        matches == 0
            ? $"Cannot create relation '{Safe(relationName)}': active table '{Safe(table.ToString())}' does not exist in the catalog."
            : $"Cannot create relation '{Safe(relationName)}': table '{Safe(table.ToString())}' is ambiguous ({matches} active matches).");

    /// <summary>Maps requested key columns to the catalog's canonical names (case-insensitive); an unknown or ambiguous name throws.</summary>
    public static List<string> Canonicalize(string relationName, string side, IReadOnlyList<string> requested, IReadOnlyCollection<string> catalogColumns)
    {
        var result = new List<string>(requested.Count);
        foreach (var name in requested)
        {
            string? exact = null;
            string? only = null;
            var matches = 0;
            foreach (var column in catalogColumns)
            {
                if (string.Equals(column, name, StringComparison.Ordinal))
                {
                    exact = column;
                }

                if (string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                    only = column;
                }
            }

            var canonical = exact ?? (matches == 1 ? only : null);
            if (canonical is null)
            {
                throw new InvalidRelationException(
                    null,
                    matches == 0
                        ? $"Relation '{Safe(relationName)}': {side} key column '{Safe(name)}' does not exist."
                        : $"Relation '{Safe(relationName)}': {side} key column '{Safe(name)}' is ambiguous.");
            }

            result.Add(canonical);
        }

        return result;
    }

    /// <summary>True when the stored ordered pairs equal the new ones (names are canonical, so an exact comparison is used).</summary>
    public static bool SamePairs(IReadOnlyList<(string Parent, string Child)> stored, IReadOnlyList<string> parent, IReadOnlyList<string> child)
    {
        if (stored.Count != parent.Count)
        {
            return false;
        }

        for (var i = 0; i < stored.Count; i++)
        {
            if (!string.Equals(stored[i].Parent, parent[i], StringComparison.Ordinal) || !string.Equals(stored[i].Child, child[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Truncates to 128 characters and replaces control characters so identifiers cannot forge log lines (SEC-REL151-02 style).</summary>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var span = value.Length > MaxIdentifierInMessage ? value.AsSpan(0, MaxIdentifierInMessage) : value.AsSpan();
        var sb = new StringBuilder(span.Length);
        foreach (var ch in span)
        {
            sb.Append(char.IsControl(ch) ? '?' : ch);
        }

        return sb.ToString();
    }
}
