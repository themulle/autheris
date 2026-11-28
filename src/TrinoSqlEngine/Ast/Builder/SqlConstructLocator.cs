namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using TrinoSqlEngine.Parsing;

/// <summary>
/// Finds the position of a keyword sequence or an unquoted identifier in the client statement text with the real SQL lexer, so a match
/// is never found inside a string literal, a quoted identifier or a comment. Runs on error paths only; never throws.
/// </summary>
public static class SqlConstructLocator
{
    /// <summary>Locates the first run of default-channel tokens spelling <paramref name="keywords"/> (case-insensitive). 1-based line and column.</summary>
    public static bool TryLocateTokens(string sql, IReadOnlyList<string> keywords, out int line, out int column)
    {
        line = 0;
        column = 0;
        if (string.IsNullOrEmpty(sql) || keywords is null || keywords.Count == 0)
        {
            return false;
        }

        try
        {
            var tokens = Lex(sql);
            for (int i = 0; i + keywords.Count <= tokens.Count; i++)
            {
                bool all = true;
                for (int k = 0; k < keywords.Count; k++)
                {
                    if (!Matches(tokens[i + k], keywords[k]))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    line = tokens[i].Line;
                    column = tokens[i].Column + 1;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // fail closed: no position, the rejection itself is unchanged
        }

        return false;
    }

    /// <summary>Locates the first unquoted occurrence of <paramref name="name"/> (case-insensitive). 1-based line and column.</summary>
    public static bool TryLocateIdentifier(string sql, string name, out int line, out int column)
    {
        return TryLocateTokens(sql, [name ?? string.Empty], out line, out column);
    }

    private static bool Matches(IToken token, string text) =>
        IsPlain(token) && string.Equals(token.Text, text, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlain(IToken token) => token.Channel == Lexer.DefaultTokenChannel && token.Type switch
    {
        SqlBaseLexer.STRING or SqlBaseLexer.UNICODE_STRING or SqlBaseLexer.BINARY_LITERAL
            or SqlBaseLexer.QUOTED_IDENTIFIER or SqlBaseLexer.BACKQUOTED_IDENTIFIER or SqlBaseLexer.Eof => false,
        _ => true
    };

    private static List<IToken> Lex(string sql)
    {
        var lexer = AntlrCacheGenerations.Shared.CreateLexer(new ZeroCopyCaseInsensitiveStream(sql.AsMemory()));
        lexer.RemoveErrorListeners();
        var result = new List<IToken>();
        for (IToken t = lexer.NextToken(); t.Type != SqlBaseLexer.Eof; t = lexer.NextToken())
        {
            if (t.Channel == Lexer.DefaultTokenChannel)
            {
                result.Add(t);
            }
        }

        return result;
    }
}
