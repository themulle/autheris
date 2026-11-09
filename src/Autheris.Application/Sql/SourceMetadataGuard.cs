using System.Collections.Frozen;
using Autheris.Domain.Common;

namespace Autheris.Application.Sql;

/// <summary>
/// Autheris reads data from data sources and never their metadata. This guard lexes a statement bound for a data-source
/// connection (literals and comments skipped, quoted identifiers normalised) and rejects references to system catalogs,
/// metadata functions and metadata statements. Any lexer error is a violation (fail closed). The exception carries a rule
/// id only, never the statement.
/// </summary>
public static class SourceMetadataGuard
{
    private static readonly AsyncLocal<bool> _suppressed = new();

    /// <summary>True when source metadata checking is temporarily suppressed on the current async context.</summary>
    public static bool IsSuppressed => _suppressed.Value;

    /// <summary>
    /// Temporary bypass for legacy catalog/key readers and validators before their complete removal in PR 2-4.
    /// Outside these legacy paths, the guard is fail-closed.
    /// </summary>
    public static IDisposable Suppress()
    {
        _suppressed.Value = true;
        return new SuppressScope();
    }

    private sealed class SuppressScope : IDisposable
    {
        public void Dispose() => _suppressed.Value = false;
    }

    /// <summary>Throws <see cref="SourceMetadataAccessDeniedException"/> when the statement reads source metadata.</summary>
    public static void EnsureDataOnly(string? sql, DatabaseDialect dialect)
    {
        if (TryFindViolation(sql, dialect, out var rule))
        {
            throw new SourceMetadataAccessDeniedException(rule);
        }
    }

    /// <summary>Non-throwing form: <paramref name="rule"/> is the rule id (never the statement) when a violation is found.</summary>
    public static bool TryFindViolation(string? sql, DatabaseDialect dialect, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? rule)
    {
        rule = null;
        if (_suppressed.Value || string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var tokens = new List<Token>(32);
        rule = Lex(sql, dialect, tokens) ? Evaluate(sql, tokens, dialect) : "LEX";
        return rule is not null;
    }

    private enum Kind : byte { Word, Dot, Open, Cast, Semicolon, Other }

    private readonly record struct Token(int Start, int Length, Kind Kind);

    private static readonly FrozenSet<string> StatementKeywords =
        new[] { "show", "describe", "desc", "analyze", "explain", "dbcc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SqlServerFunctions = new[]
    {
        "object_id", "object_name", "object_schema_name", "objectproperty", "objectpropertyex", "columnproperty", "indexproperty",
        "col_length", "object_definition", "has_perms_by_name"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SqlServerProcedurePrefixes =
    [
        "sp_help", "sp_describe_", "sp_columns", "sp_tables", "sp_pkeys", "sp_fkeys", "sp_statistics", "sp_spaceused",
        "sp_stored_procedures", "sp_sproc_columns", "sp_special_columns"
    ];

    private static readonly FrozenSet<string> SqlServerLegacyViews = new[]
    {
        "sysobjects", "syscolumns", "sysindexes", "sysdatabases", "sysusers", "syscomments", "sysdepends", "sysforeignkeys"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> PostgreSqlRelations = new[]
    {
        "pg_class", "pg_attribute", "pg_index", "pg_constraint", "pg_namespace", "pg_proc", "pg_type", "pg_description",
        "pg_statistic", "pg_settings", "pg_tables", "pg_views", "pg_indexes", "pg_matviews", "pg_roles", "pg_authid", "pg_database"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> OracleDictionary = new[]
    {
        "tables", "tab_columns", "tab_cols", "views", "objects", "constraints", "cons_columns", "indexes", "ind_columns", "arguments",
        "procedures", "source", "synonyms", "tab_statistics", "tab_col_statistics", "tab_comments", "col_comments", "dependencies",
        "policies", "segments", "types", "mviews"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool Lex(string sql, DatabaseDialect dialect, List<Token> tokens)
    {
        var s = sql.AsSpan();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var end = s[i..].IndexOf('\n');
                i = end < 0 ? s.Length : i + end + 1;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i = SkipBlockComment(s, i);
                if (i < 0)
                {
                    return false;
                }
            }
            else if (c == '\'')
            {
                var prev = tokens.Count > 0 ? tokens[^1] : default;
                var adjacent = prev.Kind == Kind.Word && prev.Start + prev.Length == i;
                var backslash = adjacent && prev.Length == 1 && s[prev.Start] is 'e' or 'E';
                var oracleQ = adjacent && dialect == DatabaseDialect.Oracle && prev.Length <= 2 && s.Slice(prev.Start, prev.Length).Equals(prev.Length == 1 ? "q" : "nq", StringComparison.OrdinalIgnoreCase);
                i = oracleQ ? SkipOracleQuoted(s, i) : SkipLiteral(s, i, backslash);
                if (i < 0)
                {
                    return false;
                }
            }
            else if (c == '$' && dialect == DatabaseDialect.PostgreSql && TryDollarTag(s, i, out var tagLength))
            {
                var close = s[(i + tagLength)..].IndexOf(s.Slice(i, tagLength), StringComparison.Ordinal);
                if (close < 0)
                {
                    return false;
                }

                i += tagLength + close + tagLength;
            }
            else if (c == '"' || c == '`' || (c == '[' && dialect == DatabaseDialect.SqlServer))
            {
                var closer = c == '[' ? ']' : c;
                var j = i + 1;
                while (j < s.Length && (s[j] != closer || (j + 1 < s.Length && s[j + 1] == closer)))
                {
                    j += s[j] == closer ? 2 : 1;
                }

                if (j >= s.Length)
                {
                    return false;
                }

                tokens.Add(new Token(i + 1, j - i - 1, Kind.Word));
                i = j + 1;
            }
            else if (IsWordChar(c))
            {
                var j = i + 1;
                while (j < s.Length && IsWordChar(s[j]))
                {
                    j++;
                }

                tokens.Add(new Token(i, j - i, Kind.Word));
                i = j;
            }
            else if (c == ':' && i + 1 < s.Length && s[i + 1] == ':')
            {
                tokens.Add(new Token(i, 2, Kind.Cast));
                i += 2;
            }
            else
            {
                tokens.Add(new Token(i, 1, c switch { '.' => Kind.Dot, '(' => Kind.Open, ';' => Kind.Semicolon, _ => Kind.Other }));
                i++;
            }
        }

        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    private static int SkipBlockComment(ReadOnlySpan<char> s, int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return -1;
    }

    private static int SkipLiteral(ReadOnlySpan<char> s, int i, bool backslashEscapes)
    {
        for (i++; i < s.Length; i++)
        {
            if (backslashEscapes && s[i] == '\\')
            {
                i++;
            }
            else if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i + 1;
            }
        }

        return -1;
    }

    private static int SkipOracleQuoted(ReadOnlySpan<char> s, int i)
    {
        if (i + 1 >= s.Length)
        {
            return -1;
        }

        var closer = s[i + 1] switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', var d => d };
        var end = s[(i + 2)..].IndexOf(new[] { closer, '\'' });
        return end < 0 ? -1 : i + 2 + end + 2;
    }

    private static bool TryDollarTag(ReadOnlySpan<char> s, int i, out int length)
    {
        length = 0;
        var j = i + 1;
        if (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_'))
        {
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
            {
                j++;
            }
        }

        if (j < s.Length && s[j] == '$')
        {
            length = j - i + 1;
            return true;
        }

        return false;
    }

    private static string? Evaluate(string sql, List<Token> tokens, DatabaseDialect dialect)
    {
        for (var n = 0; n < tokens.Count; n++)
        {
            var t = tokens[n];
            if (t.Kind != Kind.Word)
            {
                continue;
            }

            var word = sql.AsSpan(t.Start, t.Length);
            var next = n + 1 < tokens.Count ? tokens[n + 1].Kind : Kind.Other;
            if (n == 0 || tokens[n - 1].Kind == Kind.Semicolon)
            {
                if (StatementKeywords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word) || IsSetDiagnostics(sql, tokens, n))
                {
                    return "R6";
                }
            }

            if (word.Equals("information_schema", StringComparison.OrdinalIgnoreCase) && next == Kind.Dot)
            {
                return "R1";
            }

            var rule = dialect switch
            {
                DatabaseDialect.SqlServer => SqlServerRule(sql, tokens, n, word, next),
                DatabaseDialect.PostgreSql => PostgreSqlRule(tokens, n, word, next),
                DatabaseDialect.Oracle => OracleRule(sql, tokens, n, word, next),
                DatabaseDialect.Databricks => word.Equals("system", StringComparison.OrdinalIgnoreCase) && next == Kind.Dot
                    && n + 3 < tokens.Count && tokens[n + 2].Kind == Kind.Word && tokens[n + 3].Kind == Kind.Dot ? "R5" : null,
                _ => null
            };
            if (rule is not null)
            {
                return rule;
            }
        }

        return null;
    }

    private static bool IsSetDiagnostics(string sql, List<Token> tokens, int n)
    {
        if (!sql.AsSpan(tokens[n].Start, tokens[n].Length).Equals("set", StringComparison.OrdinalIgnoreCase) || n + 1 >= tokens.Count)
        {
            return false;
        }

        var option = sql.AsSpan(tokens[n + 1].Start, tokens[n + 1].Length);
        return option.StartsWith("showplan_", StringComparison.OrdinalIgnoreCase) || option.Equals("statistics", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SqlServerRule(string sql, List<Token> tokens, int n, ReadOnlySpan<char> word, Kind next)
    {
        // sys.sp_set_session_context sets the caller's session values; it reads nothing.
        var sessionSetter = word.Equals("sys", StringComparison.OrdinalIgnoreCase) && n + 2 < tokens.Count
            && sql.AsSpan(tokens[n + 2].Start, tokens[n + 2].Length).Equals("sp_set_session_context", StringComparison.OrdinalIgnoreCase);
        if (!sessionSetter && next == Kind.Dot && (word.Equals("sys", StringComparison.OrdinalIgnoreCase) || word.Equals("master", StringComparison.OrdinalIgnoreCase)
                || word.Equals("msdb", StringComparison.OrdinalIgnoreCase)))
        {
            return "R2";
        }

        if (next == Kind.Open && SqlServerFunctions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
        {
            return "R2";
        }

        if (SqlServerLegacyViews.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
        {
            return "R2";
        }

        foreach (var prefix in SqlServerProcedurePrefixes)
        {
            if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return "R2";
            }
        }

        return null;
    }

    private static string? PostgreSqlRule(List<Token> tokens, int n, ReadOnlySpan<char> word, Kind next)
    {
        if (word.Equals("pg_catalog", StringComparison.OrdinalIgnoreCase) || PostgreSqlRelations.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)
            || (word.StartsWith("pg_stat", StringComparison.OrdinalIgnoreCase))
            || (next == Kind.Open && (word.StartsWith("pg_get_", StringComparison.OrdinalIgnoreCase) || word.StartsWith("to_reg", StringComparison.OrdinalIgnoreCase))))
        {
            return "R3";
        }

        if (n > 0 && tokens[n - 1].Kind == Kind.Cast && word.StartsWith("reg", StringComparison.OrdinalIgnoreCase)
            && (word.Equals("regclass", StringComparison.OrdinalIgnoreCase) || word.Equals("regproc", StringComparison.OrdinalIgnoreCase)
                || word.Equals("regtype", StringComparison.OrdinalIgnoreCase)))
        {
            return "R3";
        }

        return null;
    }

    private static string? OracleRule(string sql, List<Token> tokens, int n, ReadOnlySpan<char> word, Kind next)
    {
        if (word.StartsWith("v$", StringComparison.OrdinalIgnoreCase) || word.StartsWith("gv$", StringComparison.OrdinalIgnoreCase)
            || word.Equals("dbms_metadata", StringComparison.OrdinalIgnoreCase))
        {
            return "R4";
        }

        if (word.Equals("sys", StringComparison.OrdinalIgnoreCase) && next == Kind.Dot)
        {
            // SYS.UTL_RAW.CAST_TO_RAW is Autheris's byte-exact tenant comparison; it reads no metadata.
            var utlRaw = n + 2 < tokens.Count && sql.AsSpan(tokens[n + 2].Start, tokens[n + 2].Length).Equals("utl_raw", StringComparison.OrdinalIgnoreCase);
            if (!utlRaw)
            {
                return "R4";
            }
        }

        var underscore = word.IndexOf('_');
        if (underscore > 0 && word[..underscore] is var prefix
            && (prefix.Equals("all", StringComparison.OrdinalIgnoreCase) || prefix.Equals("user", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("dba", StringComparison.OrdinalIgnoreCase) || prefix.Equals("cdb", StringComparison.OrdinalIgnoreCase))
            && OracleDictionary.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word[(underscore + 1)..]))
        {
            return "R4";
        }

        return null;
    }
}

/// <summary>
/// A statement bound for a data source referenced source metadata. The message carries the rule id only, never the statement,
/// an object name or a value.
/// </summary>
public sealed class SourceMetadataAccessDeniedException(string rule)
    : InvalidOperationException($"A statement that reads source metadata was blocked (rule {rule}).")
{
    public string Rule { get; } = rule;
}
