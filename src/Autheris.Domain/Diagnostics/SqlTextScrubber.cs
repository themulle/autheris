namespace Autheris.Domain.Diagnostics;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;

public static class SqlTextScrubber
{
    public const int MaxSqlLengthBytes = 64 * 1024; // 64 KiB

    public static bool TryScrub(string? sql, DatabaseDialect dialect, out string? scrubbed, out string? queryHash)
    {
        if (TryScrub(sql, dialect, out var result))
        {
            scrubbed = result;
            queryHash = ComputeQueryHash(result);
            return true;
        }

        scrubbed = null;
        queryHash = null;
        return false;
    }

    public static bool TryScrub(string? sql, DatabaseDialect dialect, out string scrubbed)
    {
        scrubbed = string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        if (sql.Length > MaxSqlLengthBytes)
        {
            return false;
        }

        var sb = new StringBuilder(sql.Length);
        int i = 0;
        int len = sql.Length;

        while (i < len)
        {
            char c = sql[i];

            // 1. Line comment: --
            if (c == '-' && i + 1 < len && sql[i + 1] == '-')
            {
                i += 2;
                while (i < len && sql[i] != '\n' && sql[i] != '\r')
                {
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            // 2. Block comment: /* ... */
            if (c == '/' && i + 1 < len && sql[i + 1] == '*')
            {
                i += 2;
                bool closed = false;
                while (i + 1 < len)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        i += 2;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    // Unterminated block comment -> fail closed
                    return false;
                }
                sb.Append(' ');
                continue;
            }

            // 3. PostgreSQL dollar quoting: $$ ... $$ -> fail closed (unsupported)
            if (c == '$' && i + 1 < len && sql[i + 1] == '$')
            {
                return false;
            }

            // 4. Oracle Q-quoting: q'...' or Q'...' -> fail closed (unsupported)
            if ((c == 'q' || c == 'Q') && i + 1 < len && sql[i + 1] == '\'')
            {
                return false;
            }

            // 5. String literal: '...' (with optional N'...' or E'...')
            bool isNationalString = (c == 'n' || c == 'N') && i + 1 < len && sql[i + 1] == '\'';
            bool isEscapeString = (c == 'e' || c == 'E') && i + 1 < len && sql[i + 1] == '\'';
            if (c == '\'' || isNationalString || isEscapeString)
            {
                if (isNationalString || isEscapeString)
                {
                    i++; // skip N/E
                }
                i++; // skip opening '

                bool closed = false;
                while (i < len)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < len && sql[i + 1] == '\'')
                        {
                            i += 2; // escaped single quote
                            continue;
                        }
                        i++; // closing single quote
                        closed = true;
                        break;
                    }
                    i++;
                }

                if (!closed)
                {
                    return false; // unterminated string -> fail closed
                }

                sb.Append('?');
                continue;
            }

            // 6. Quoted identifiers: "...", [...], `...` -> keep unchanged
            if (c == '"' || c == '`' || c == '[')
            {
                char closing = c switch
                {
                    '[' => ']',
                    _ => c
                };

                sb.Append(c);
                i++;
                bool closed = false;
                while (i < len)
                {
                    char curr = sql[i];
                    sb.Append(curr);
                    i++;
                    if (curr == closing)
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed)
                {
                    return false; // unterminated identifier -> fail closed
                }
                continue;
            }

            // 7. Hex literal: 0x...
            if (c == '0' && i + 1 < len && (sql[i + 1] == 'x' || sql[i + 1] == 'X'))
            {
                i += 2;
                while (i < len && IsHexChar(sql[i]))
                {
                    i++;
                }
                sb.Append('?');
                continue;
            }

            // 8. Numeric literal (isolated digit, not inside an identifier)
            if (char.IsAsciiDigit(c) && (i == 0 || !IsIdentifierPart(sql[i - 1])))
            {
                while (i < len && (char.IsAsciiDigit(sql[i]) || sql[i] == '.' || sql[i] == 'e' || sql[i] == 'E' ||
                       ((sql[i] == '+' || sql[i] == '-') && i > 0 && (sql[i - 1] == 'e' || sql[i - 1] == 'E'))))
                {
                    i++;
                }
                sb.Append('?');
                continue;
            }

            // Normal character
            sb.Append(c);
            i++;
        }

        scrubbed = sb.ToString().Trim();
        return true;
    }

    public static string ComputeQueryHash(string scrubbedSql)
    {
        if (string.IsNullOrWhiteSpace(scrubbedSql))
        {
            return "0000000000000000";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(scrubbedSql));
        // Lowercase hex of first 8 bytes
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static bool IsHexChar(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsIdentifierPart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c == '@';
}
