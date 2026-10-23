using System.Collections.Immutable;
using System.Security;
using System.Text;
using Autheris.Domain.Exceptions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// SQLA-06 follow-up: the SQL text the governed compiler emits for <c>SELECT &lt;all catalog columns&gt; FROM "schema"."table"</c> with the
/// table's typed row policy applied by the engine (tenant wrapper, policy, coverage proof). The tree splices it as
/// <c>FROM (&lt;secured SQL&gt;) AS autheris_target</c>. <see cref="Parameters"/> carry the engine markers and their values; the tree compiler
/// maps every marker to a fresh <c>@gqN</c> name (never the engine's own names).
/// </summary>
public sealed record TreeSecuredSource(string Sql, ImmutableArray<BoundParameter> Parameters, TargetSqlDialect Dialect);

/// <summary>The mask provider of the secured source: the tree keeps masking in its outer projection (no double HMAC, Issue143 parity).</summary>
internal sealed class TreeNoMasks : IColumnMaskProvider
{
    public static TreeNoMasks Instance { get; } = new();

    public bool HasMask(TableIdentity table, string column) => false;

    public MaskSpec GetMask(TableIdentity table, string column) =>
        throw new SecurityException("The tree secured source carries no masks (fail-closed).");
}

/// <summary>
/// SQLA06F-C4: tokenising marker remap of the post-compile engine text. Literals, quoted identifiers and comments are never touched; a marker
/// is matched as a whole token (<c>@p1</c> is not a prefix of <c>@p10</c>); an engine-style token that is not a declared marker, a
/// dollar-quoted string and an unterminated literal deny the request.
/// </summary>
internal static class TreeSecuredSourceMarkers
{
    public const string DeniedCode = "TREE_ROW_POLICY_UNSUPPORTED";
    public const string DeniedMessage = "The row policy of this table is not supported in tree queries.";

    public static GatewaySecurityException Denied() => new(DeniedMessage, DeniedCode);

    /// <summary>Replaces every marker of <paramref name="map"/> (marker to new name). Each marker must occur at least once (F4).</summary>
    public static string Remap(string sql, TargetSqlDialect dialect, IReadOnlyDictionary<string, string> map)
    {
        var postgres = dialect == TargetSqlDialect.PostgreSql;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder(sql.Length + 16);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = Skip(sql, sb, i, c);
                    continue;
                case '[' when !postgres:
                    i = Skip(sql, sb, i, ']');
                    continue;
                case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                {
                    var end = sql.IndexOf('\n', i);
                    end = end < 0 ? sql.Length : end + 1;
                    sb.Append(sql, i, end - i);
                    i = end;
                    continue;
                }
                case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Denied();
                    }

                    sb.Append(sql, i, end + 2 - i);
                    i = end + 2;
                    continue;
                }
                case '@':
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '@')
                    {
                        sb.Append("@@");
                        i += 2;
                        continue;
                    }

                    var end = TokenEnd(sql, i + 1);
                    var token = sql[i..end];
                    if (map.TryGetValue(token, out var name))
                    {
                        sb.Append(name);
                        seen.Add(token);
                    }
                    else if (IsEngineStyle(token))
                    {
                        throw Denied();
                    }
                    else
                    {
                        sb.Append(token);
                    }

                    i = end;
                    continue;
                }
                case '?':
                {
                    // SQLite emits numbered question markers (?1). A bare or undeclared question mark is never part of governed output.
                    var end = i + 1;
                    while (end < sql.Length && char.IsAsciiDigit(sql[end]))
                    {
                        end++;
                    }

                    var token = sql[i..end];
                    if (end == i + 1 || !map.TryGetValue(token, out var name))
                    {
                        throw Denied();
                    }

                    sb.Append(name);
                    seen.Add(token);
                    i = end;
                    continue;
                }
                case '$' when postgres:
                {
                    var end = TokenEnd(sql, i + 1);
                    var token = sql[i..end];
                    if (end > i + 1 && char.IsAsciiDigit(sql[i + 1]))
                    {
                        if (map.TryGetValue(token, out var name))
                        {
                            sb.Append(name);
                            seen.Add(token);
                        }
                        else
                        {
                            throw Denied();
                        }
                    }
                    else if (end < sql.Length && sql[end] == '$')
                    {
                        // $$ or $tag$: a dollar-quoted string is never emitted by the engine; deny instead of guessing its extent.
                        throw Denied();
                    }
                    else
                    {
                        sb.Append(token);
                    }

                    i = end;
                    continue;
                }
                default:
                    sb.Append(c);
                    i++;
                    continue;
            }
        }

        foreach (var marker in map.Keys)
        {
            if (!seen.Contains(marker))
            {
                throw Denied();
            }
        }

        return sb.ToString();
    }

    /// <summary>True when the text contains <c>LIMIT</c>, <c>TOP</c> or <c>FETCH</c> as a keyword at the root (outside literals, quoted identifiers and parentheses).</summary>
    public static bool HasRootRowLimit(string sql)
    {
        var depth = 0;
        var i = 0;
        var scratch = new StringBuilder();
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = Skip(sql, scratch, i, c);
                scratch.Clear();
                continue;
            }

            if (c == '[')
            {
                i = Skip(sql, scratch, i, ']');
                scratch.Clear();
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth <= 0 && (char.IsLetter(c) || c == '_') && (i == 0 || !IsIdentifierChar(sql[i - 1])))
            {
                var end = i;
                while (end < sql.Length && IsIdentifierChar(sql[end]))
                {
                    end++;
                }

                var word = sql.AsSpan(i, end - i);
                if (word.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("TOP", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("FETCH", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                i = end;
                continue;
            }

            i++;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int TokenEnd(string sql, int start)
    {
        var end = start;
        while (end < sql.Length && IsIdentifierChar(sql[end]))
        {
            end++;
        }

        return end;
    }

    /// <summary>The engine's own marker names: <c>@p</c> plus digits (SQLite, SQL Server) or <c>$</c> plus digits (PostgreSQL).</summary>
    private static bool IsEngineStyle(string token) =>
        token.Length > 2 && token[0] == '@' && token[1] is 'p' or 'P' && token.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    private static int Skip(string sql, StringBuilder sb, int start, char close)
    {
        var end = start + 1;
        while (true)
        {
            if (end >= sql.Length)
            {
                throw Denied();
            }

            if (sql[end] == close)
            {
                if (close != ']' && end + 1 < sql.Length && sql[end + 1] == close)
                {
                    end += 2;
                    continue;
                }

                break;
            }

            end++;
        }

        end++;
        sb.Append(sql, start, end - start);
        return end;
    }
}
