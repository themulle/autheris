namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Model;

/// <summary>
/// Per-caller verification of a catalog search candidate. The search index is built from the whole table (every column name and
/// description) and its vectors from the same text, so a candidate may match only through metadata the caller cannot see. The
/// caller-projected <see cref="DiscoveryObject"/> (the one describe_dataset is built from) lists exactly the visible columns and texts;
/// a candidate of a column-restricted table is kept only when the query matches that projection lexically. A vector-only candidate
/// of such a table is dropped: its similarity was computed from hidden text, so it cannot be told apart from a hidden-text match.
/// A table with nothing hidden from the caller needs no verification.
/// </summary>
internal static class DiscoveryVisibleTextMatcher
{
    // A visible token shorter than this never matches by being contained in a longer query word.
    private const int MinContainedTokenLength = 4;

    /// <summary>True when the caller sees less than the table holds: a column removed (no access, or discovery-hidden) or a column text withheld.</summary>
    public static bool IsRestricted(TableMetadata table, DiscoveryObject projected)
    {
        if (projected.HiddenTextColumns.Count > 0)
        {
            return true;
        }

        var visible = VisibleColumnNames(projected);
        foreach (var column in table.Columns)
        {
            if (!visible.Contains(column.ColumnName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The whole query as a substring of any caller-visible text (identifier, table text, tags, visible columns).</summary>
    public static bool ContainsQuery(TableMetadata table, DiscoveryObject projected, string query)
    {
        foreach (var field in VisibleFields(table, projected))
        {
            if (field.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>At least one query term matches a token of the caller-visible text, with the same tokenization the index uses.</summary>
    public static bool MatchesAnyTerm(TableMetadata table, DiscoveryObject projected, string query) =>
        VisibleTermMatchCount(table, projected, query) > 0;

    /// <summary>The number of query terms that match the caller-visible text; a score that never depends on hidden text.</summary>
    public static int VisibleTermMatchCount(TableMetadata table, DiscoveryObject projected, string query)
    {
        var terms = SmartSchemaTokenizer.Tokenize(query, expandAbbreviations: false);
        if (terms.Length == 0)
        {
            return 0;
        }

        var text = new StringBuilder();
        foreach (var field in VisibleFields(table, projected))
        {
            text.Append(field).Append(' ');
        }

        var tokens = SmartSchemaTokenizer.Tokenize(text.ToString(), expandAbbreviations: true);
        var count = 0;
        foreach (var term in terms)
        {
            foreach (var token in tokens)
            {
                if (token.Contains(term, StringComparison.Ordinal) ||
                    (token.Length >= MinContainedTokenLength && term.Contains(token, StringComparison.Ordinal)))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static HashSet<string> VisibleColumnNames(DiscoveryObject projected)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in projected.Columns)
        {
            if (column.State != DiscoveryColumnState.NoAccess)
            {
                names.Add(column.Name);
            }
        }

        return names;
    }

    private static IEnumerable<string> VisibleFields(TableMetadata table, DiscoveryObject projected)
    {
        foreach (var field in new[] { table.Identifier.Domain, table.Identifier.Schema, table.Identifier.TableName, projected.DisplayName, projected.Description, table.Table.DisplayName, table.Table.Description })
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                yield return field;
            }
        }

        foreach (var tag in projected.Tags)
        {
            yield return tag;
        }

        if (table.Table.Texts is not null)
        {
            foreach (var entry in table.Table.Texts.Entries.Values)
            {
                foreach (var field in new[] { entry.DisplayName, entry.Description, entry.LongDescription })
                {
                    if (!string.IsNullOrWhiteSpace(field))
                    {
                        yield return field;
                    }
                }
            }
        }

        var sourceColumns = table.Columns.ToDictionary(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        foreach (var column in projected.Columns)
        {
            if (column.State == DiscoveryColumnState.NoAccess)
            {
                continue;
            }

            yield return column.Name;
            yield return column.DataType;
            var textHidden = projected.HiddenTextColumns.Contains(column.Name);
            if (textHidden)
            {
                continue;
            }

            foreach (var field in new[] { column.DisplayName, column.Description, column.LongDescription })
            {
                if (!string.IsNullOrWhiteSpace(field))
                {
                    yield return field;
                }
            }

            if (sourceColumns.TryGetValue(column.Name, out var source) && source.Texts is not null)
            {
                foreach (var entry in source.Texts.Entries.Values)
                {
                    foreach (var field in new[] { entry.DisplayName, entry.Description })
                    {
                        if (!string.IsNullOrWhiteSpace(field))
                        {
                            yield return field;
                        }
                    }
                }
            }
        }
    }
}
