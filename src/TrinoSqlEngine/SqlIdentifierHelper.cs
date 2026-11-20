namespace TrinoSqlEngine;

using System;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

public static class SqlIdentifierHelper
{
    /// <summary>
    /// Normalizes an identifier by stripping enclosing double quotes, backticks, or square brackets
    /// and unescaping doubled quote characters (<c>""</c> -&gt; <c>"</c>, <c>``</c> -&gt; <c>`</c>).
    /// Does not perform case folding.
    /// </summary>
    public static string NormalizeIdentifier(string id)
    {
        return ParseIdentifier(id).Value;
    }

    /// <summary>
    /// Parses a raw SQL identifier token into a typed <see cref="Ast.Nodes.SqlIdentifier"/>,
    /// stripping enclosing quotes and unescaping doubled quote characters.
    /// </summary>
    public static Ast.Nodes.SqlIdentifier ParseIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Ast.Nodes.SqlIdentifier(string.Empty, false);
        string id = raw.Trim();
        if (id.Length >= 2)
        {
            char first = id[0];
            char last = id[^1];
            if (first == '"' && last == '"')
            {
                return new Ast.Nodes.SqlIdentifier(id[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal), true);
            }

            if (first == '`' && last == '`')
            {
                return new Ast.Nodes.SqlIdentifier(id[1..^1].Replace("``", "`", StringComparison.Ordinal), true);
            }

            if (first == '[' && last == ']')
            {
                return new Ast.Nodes.SqlIdentifier(id[1..^1], true);
            }
        }

        return new Ast.Nodes.SqlIdentifier(id, false);
    }

    /// <summary>
    /// Builds a typed <see cref="Ast.Nodes.SqlQualifiedName"/> from an ANTLR <c>qualifiedName</c> context.
    /// </summary>
    public static Ast.Nodes.SqlQualifiedName ToQualifiedName(SqlBaseParser.QualifiedNameContext context)
    {
        var ids = context.identifier();
        if (ids == null || ids.Length == 0)
        {
            return new Ast.Nodes.SqlQualifiedName(new[] { ParseIdentifier(context.GetText()) });
        }

        var parts = new Ast.Nodes.SqlIdentifier[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            parts[i] = ParseIdentifier(ids[i].GetText());
        }

        return new Ast.Nodes.SqlQualifiedName(parts);
    }

    /// <summary>
    /// K-P11: Extracts the simple (unqualified) name from a dot-qualified identifier or table name.
    /// </summary>
    public static string GetSimpleName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return string.Empty;
        int lastDot = fullName.LastIndexOf('.', StringComparison.Ordinal);
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

        // For ANSI and SQL Server canonical comparison:
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

    /// <summary>
    /// Strips the catalog part from a 3-part qualified table name (e.g. "catalog.schema.table" -> "schema.table",
    /// "\"catalog\".\"schema\".\"table\"" -> "\"schema\".\"table\"").
    /// Leaves 1-part and 2-part table names unchanged.
    /// </summary>
    public static string StripCatalogPrefix(string rawTableName)
    {
        if (string.IsNullOrWhiteSpace(rawTableName)) return rawTableName;

        var parts = SplitDottedParts(rawTableName);
        if (parts.Count == 3)
        {
            return rawTableName[parts[0].Length..].TrimStart('.');
        }

        return rawTableName;
    }

    private static System.Collections.Generic.List<string> SplitDottedParts(string text)
    {
        var parts = new System.Collections.Generic.List<string>(3);
        int start = 0;
        bool inDoubleQuotes = false;
        bool inBackticks = false;
        bool inBrackets = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && !inBackticks && !inBrackets) inDoubleQuotes = !inDoubleQuotes;
            else if (c == '`' && !inDoubleQuotes && !inBrackets) inBackticks = !inBackticks;
            else if (c == '[' && !inDoubleQuotes && !inBackticks) inBrackets = true;
            else if (c == ']' && inBrackets) inBrackets = false;
            else if (c == '.' && !inDoubleQuotes && !inBackticks && !inBrackets)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        if (start <= text.Length)
        {
            parts.Add(text[start..]);
        }
        return parts;
    }
}
