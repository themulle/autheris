namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Security;

/// <summary>
/// SR-M-25: a policy filter is spliced into the user's query as text, so an unqualified table it references (for example
/// the consent table of an EXISTS predicate) would bind to a user CTE of the same name. This guard fails closed when a CTE
/// name of the user query equals a table name referenced by any injected predicate. The check is order independent: it runs
/// when a filter is injected (against the CTE names seen so far) and when a CTE is declared (against the filters injected so far).
/// </summary>
internal sealed class PolicyFilterCteGuard
{
    private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filterTables = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="cteName">A CTE name exactly as written (quotes are removed, comparison is case-insensitive).</param>
    public void RegisterCte(string cteName)
    {
        string name = Normalize(cteName);
        if (name.Length == 0) return;
        _cteNames.Add(name);
        if (_filterTables.Contains(name)) throw Collision(name);
    }

    public void RegisterFilter(string? filterSql)
    {
        if (string.IsNullOrWhiteSpace(filterSql)) return;
        foreach (string table in ExtractUnqualifiedTableReferences(filterSql))
        {
            _filterTables.Add(table);
            if (_cteNames.Contains(table)) throw Collision(table);
        }
    }

    private static SecurityException Collision(string name) =>
        new($"A CTE named '{name}' would shadow a table referenced by a row-level security predicate; rename the CTE.");

    private static string Normalize(string identifier)
    {
        string id = identifier.Trim();
        if (id.Length >= 2 &&
            ((id[0] == '"' && id[^1] == '"') || (id[0] == '`' && id[^1] == '`') || (id[0] == '[' && id[^1] == ']')))
        {
            id = id[1..^1];
        }
        return id.Replace("\"\"", "\"", StringComparison.Ordinal).Replace("``", "`", StringComparison.Ordinal);
    }

    // Single-part names in table position (after FROM, JOIN or a comma). Deliberately over-approximate: fail closed.
    private static List<string> ExtractUnqualifiedTableReferences(string sql)
    {
        var tokens = Tokenize(sql);
        var result = new List<string>();
        for (int i = 1; i < tokens.Count; i++)
        {
            var (kind, text) = tokens[i];
            if (kind != 'i') continue;

            var (prevKind, prevText) = tokens[i - 1];
            bool tablePosition = prevText == "," ||
                (prevKind == 'w' && (prevText.Equals("from", StringComparison.OrdinalIgnoreCase) ||
                                     prevText.Equals("join", StringComparison.OrdinalIgnoreCase)));
            if (!tablePosition) continue;

            string next = i + 1 < tokens.Count ? tokens[i + 1].Text : string.Empty;
            if (next == "." || next == "(") continue;
            result.Add(Normalize(text));
        }
        return result;
    }

    // Kinds: 'w' bare word, 'i' bare identifier-like word or delimited identifier, 'p' punctuation; literals are dropped.
    private static List<(char Kind, string Text)> Tokenize(string sql)
    {
        var tokens = new List<(char, string)> { ('p', string.Empty) };
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (c == '\'')
            {
                i = SkipDelimited(sql, i, '\'');
                tokens.Add(('p', "'"));
                continue;
            }
            if (c == '"' || c == '`' || c == '[')
            {
                int end = SkipDelimited(sql, i, c == '[' ? ']' : c);
                tokens.Add(('i', sql[i..end]));
                i = end;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '@' || c == '#')
            {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '@' or '#')) i++;
                string word = sql[start..i];
                // A word is a candidate identifier unless it is purely numeric; FROM/JOIN keep kind 'w' for the position test.
                bool keyword = word.Equals("from", StringComparison.OrdinalIgnoreCase) || word.Equals("join", StringComparison.OrdinalIgnoreCase);
                tokens.Add((keyword || char.IsDigit(word[0]) ? 'w' : 'i', word));
                continue;
            }
            tokens.Add(('p', sql[i..(i + 1)]));
            i++;
        }
        return tokens;
    }

    // Returns the index after the closing delimiter; a doubled closing delimiter is an escape (not for brackets).
    private static int SkipDelimited(string sql, int open, char close)
    {
        int i = open + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (close != ']' && i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }
}
