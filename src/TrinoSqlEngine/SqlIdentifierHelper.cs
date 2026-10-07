namespace TrinoSqlEngine;

using System;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

public static class SqlIdentifierHelper
{
    /// <summary>
    /// Normalizes an identifier by stripping enclosing double quotes, backticks, or square brackets.
    /// Note: does not unescape doubled characters and does not perform case folding.
    /// </summary>
    public static string NormalizeIdentifier(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        id = id.Trim();
        if (id.Length >= 2)
        {
            if ((id[0] == '"' && id[^1] == '"') ||
                (id[0] == '`' && id[^1] == '`') ||
                (id[0] == '[' && id[^1] == ']'))
            {
                return id[1..^1];
            }
        }
        return id;
    }

    /// <summary>
    /// K-P11: Extracts the simple (unqualified) name from a dot-qualified identifier or table name.
    /// </summary>
    public static string GetSimpleName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return string.Empty;
        int lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1 ? fullName[(lastDot + 1)..] : fullName;
    }

    /// <summary>
    /// SQ-12: Dialect-aware identifier case-folding.
    /// In PostgreSQL, unquoted identifiers fold to lower-case while quoted identifiers preserve exact case.
    /// In SQL Server, identifiers are case-insensitive (folded to lower-case for canonical comparison).
    /// </summary>
    public static string FoldIdentifier(string rawIdentifier, TargetSqlDialect dialect = TargetSqlDialect.Ansi)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier)) return string.Empty;
        string id = rawIdentifier.Trim();
        bool isQuoted = false;
        if (id.Length >= 2)
        {
            char first = id[0];
            char last = id[^1];
            if ((first == '"' && last == '"') || (first == '`' && last == '`'))
            {
                isQuoted = true;
                id = id[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal).Replace("``", "`", StringComparison.Ordinal);
            }
            else if (first == '[' && last == ']')
            {
                isQuoted = true;
                id = id[1..^1];
            }
        }

        if (dialect == TargetSqlDialect.PostgreSql)
        {
            // PostgreSQL: quoted identifiers preserve case, unquoted identifiers fold to lower case
            return isQuoted ? id : id.ToLowerInvariant();
        }

        // For ANSI, SQLite, and SQL Server canonical comparison:
        return id.ToLowerInvariant();
    }

    /// <summary>
    /// SEC C-02: Scope key for single-part identifiers (CTE names). Unquoted identifiers are case-folded to lower case,
    /// quoted identifiers keep their exact content (doubled quote characters are unescaped). Comparing keys ordinally
    /// is therefore conservative for both case-insensitive (Trino) and case-sensitive quoted (PostgreSQL) semantics:
    /// when in doubt a reference is treated as a physical table (and secured), never as a CTE.
    /// </summary>
    public static string FoldIdentifierForScope(string rawIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier)) return string.Empty;
        string id = rawIdentifier.Trim();
        if (id.Length >= 2)
        {
            char first = id[0];
            char last = id[^1];
            if (first == '"' && last == '"') return id[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            if (first == '`' && last == '`') return id[1..^1].Replace("``", "`", StringComparison.Ordinal);
            if (first == '[' && last == ']') return id[1..^1];
        }
        return id.ToLowerInvariant();
    }

    /// <summary>
    /// SEC C-02: Scope key for typed SqlIdentifier. If quoted, preserves exact case; if unquoted, folds to lower case.
    /// </summary>
    public static string FoldIdentifierForScope(Ast.Nodes.SqlIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier.IsQuoted ? identifier.Value : identifier.Value.ToLowerInvariant();
    }

    /// <summary>
    /// SEC M-24: Quotes a catalog-provided column name as a delimited SQL identifier ("..." with doubled quotes).
    /// </summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// SEC M-23: Removes exactly one pair of enclosing single quotes and unescapes doubled quotes ('' -> ').
    /// Values without enclosing single quotes are returned trimmed but otherwise unchanged.
    /// </summary>
    public static string UnquoteStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string v = value.Trim();
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
        {
            return v[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        return v;
    }

    public static string NormalizeQualifiedName(SqlBaseParser.QualifiedNameContext context)
    {
        var ids = context.identifier();
        if (ids == null || ids.Length == 0) return NormalizeIdentifier(context.GetText());
        var parts = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            parts[i] = NormalizeIdentifier(ids[i].GetText());
        }
        return string.Join(".", parts);
    }
}
