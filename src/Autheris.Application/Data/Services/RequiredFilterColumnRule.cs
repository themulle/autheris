namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// MISC-03/MISC-04: the one definition of "a required filter column is satisfied" (RequiredFilterColumns / RequiredFilterColumnGroups).
/// A column counts only through (a) the tenant column, (b) the typed row policy, (c) a whole identifier of the decision's row filter
/// (never a substring, a string literal or a comment) or (d) a column the client filter constrains. An unanalysable row filter satisfies nothing.
/// </summary>
internal static class RequiredFilterColumnRule
{
    /// <summary>Throws <see cref="TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException"/> unless the rule is satisfied for <paramref name="metadata"/>.</summary>
    public static void Enforce(
        TableMetadata metadata,
        TableAccessDecision decision,
        IEnumerable<string>? clientFilterColumns,
        bool tenantColumnCounts)
    {
        if (!IsSatisfied(metadata, decision, clientFilterColumns, tenantColumnCounts))
        {
            throw new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException(
                GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing));
        }
    }

    /// <summary>True when every required column is satisfied and (when groups exist) at least one non-empty group is satisfied as a whole.</summary>
    public static bool IsSatisfied(
        TableMetadata metadata,
        TableAccessDecision decision,
        IEnumerable<string>? clientFilterColumns,
        bool tenantColumnCounts)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        if (metadata.RequiredFilterColumns.Length == 0 && metadata.RequiredFilterColumnGroups.Length == 0)
        {
            return true;
        }

        var rowFilterColumns = ReferencedIdentifiers(decision.CombinedRowFilterSql);
        bool Column(string column) => IsColumnSatisfied(column, metadata, decision, rowFilterColumns, clientFilterColumns, tenantColumnCounts);

        if (metadata.RequiredFilterColumnGroups.Length > 0)
        {
            var anyGroup = false;
            foreach (var group in metadata.RequiredFilterColumnGroups)
            {
                if (group.Length > 0 && group.All(Column))
                {
                    anyGroup = true;
                    break;
                }
            }

            if (!anyGroup)
            {
                return false;
            }
        }

        return metadata.RequiredFilterColumns.All(Column);
    }

    /// <summary>The per-column test shared by every read path.</summary>
    public static bool IsColumnSatisfied(
        string requiredColumn,
        TableMetadata metadata,
        TableAccessDecision decision,
        IEnumerable<string>? clientFilterColumns,
        bool tenantColumnCounts) =>
        IsColumnSatisfied(requiredColumn, metadata, decision, ReferencedIdentifiers(decision.CombinedRowFilterSql), clientFilterColumns, tenantColumnCounts);

    private static bool IsColumnSatisfied(
        string requiredColumn,
        TableMetadata metadata,
        TableAccessDecision decision,
        IReadOnlySet<string> rowFilterColumns,
        IEnumerable<string>? clientFilterColumns,
        bool tenantColumnCounts)
    {
        if (tenantColumnCounts &&
            metadata.TenantColumnName != null &&
            string.Equals(metadata.TenantColumnName, requiredColumn, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (decision.RowPolicy is PolicyPredicateRowPolicy rls &&
            rls.Predicate.ReferencedColumns.Contains(requiredColumn.ToLowerInvariant()))
        {
            return true;
        }

        if (rowFilterColumns.Contains(requiredColumn))
        {
            return true;
        }

        return clientFilterColumns != null &&
               clientFilterColumns.Any(c => string.Equals(c, requiredColumn, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The whole identifiers of a row filter text, case-insensitive, without anything inside string literals, quoted-literal escapes,
    /// line comments or block comments. Function names (identifier followed by an opening parenthesis) are not columns. Empty for a
    /// blank text; an unterminated literal or comment swallows the rest of the text (fewer identifiers, never more).
    /// </summary>
    internal static IReadOnlySet<string> ReferencedIdentifiers(string? sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return result;
        }

        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'');
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = sql.IndexOf(close, i + 1);
                if (end < 0)
                {
                    break;
                }

                var quoted = sql.Substring(i + 1, end - i - 1);
                if (quoted.Length > 0)
                {
                    result.Add(quoted);
                }

                i = end + 1;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$'))
                {
                    i++;
                }

                var next = i;
                while (next < sql.Length && char.IsWhiteSpace(sql[next]))
                {
                    next++;
                }

                if (next >= sql.Length || sql[next] != '(')
                {
                    result.Add(sql.Substring(start, i - start));
                }
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    /// <summary>Index after the closing quote of the literal starting at <paramref name="start"/> (doubled quotes escape); the text length when unterminated.</summary>
    private static int SkipQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }
}
