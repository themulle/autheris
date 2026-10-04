namespace Autheris.Application.Kernel;

using System;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;

public interface IExecutionGuardrailService
{
    void ValidateSingleStatement(string? sql);
    int EnforceRowLimit(int? requestedLimit, int defaultLimit = 50_000, int maxLimit = 100_000);
}

/// <summary>
/// Architecture Phase 2 (WP 2.3): Execution & Resource Guardrails.
/// Prevents multi-statement SQL injection, enforces strict row limits and resource boundaries.
/// </summary>
public sealed class ExecutionGuardrailService : IExecutionGuardrailService
{
    private static readonly Regex MultiStatementPattern = new(
        @";\s*(?:--[^\r\n]*|/\*[\s\S]*?\*/\s*)*\S+",
        RegexOptions.Compiled);

    public void ValidateSingleStatement(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        // Fast reject unquoted semicolon followed by another statement
        // Strip string literals before regex check to prevent false positives on semicolons inside strings
        var stripped = StripStringLiterals(sql);
        if (MultiStatementPattern.IsMatch(stripped))
        {
            throw new ArgumentException("Multiple SQL statements detected. Governed execution kernel strictly permits only a single statement per request.");
        }
    }

    public int EnforceRowLimit(int? requestedLimit, int defaultLimit = 50_000, int maxLimit = 100_000)
    {
        if (!requestedLimit.HasValue || requestedLimit.Value <= 0)
        {
            return defaultLimit;
        }

        return Math.Min(requestedLimit.Value, maxLimit);
    }

    private static string StripStringLiterals(string sql)
    {
        var chars = sql.ToCharArray();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == '\'' && !inDoubleQuote)
            {
                if (inSingleQuote && i + 1 < chars.Length && chars[i + 1] == '\'')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }
                inSingleQuote = !inSingleQuote;
                chars[i] = ' ';
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                chars[i] = ' ';
            }
            else if (inSingleQuote || inDoubleQuote)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }
}
