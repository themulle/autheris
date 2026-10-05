namespace TrinoSqlEngine.Analysis;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

public sealed record ExtractedParameter(
    string Name,
    string Token,
    string? TargetColumn = null,
    string? TargetTable = null,
    string? ComparisonOperator = null,
    int FirstPosition = 0);

public sealed class SqlParameterExtractor : SqlBaseBaseListener
{
    private static readonly Regex ParameterRegex = new(
        @"(?:@([a-zA-Z_][a-zA-Z0-9_]*))|(?:\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\})",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The grammar only accepts an integer or '?' as row count after OFFSET / FETCH NEXT|FIRST / LIMIT. A parameter in
    /// one of these positions is therefore normalized to '?' instead of an identifier (otherwise: syntax error).
    /// </summary>
    private static readonly Regex RowCountContextRegex = new(
        @"(?:\bOFFSET|\bFETCH\s+(?:NEXT|FIRST)|\bLIMIT)\s+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SqlCommentRegex = new(
        @"(?:--[^\r\n]*)|(?:\/\*[\s\S]*?\*\/)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly Dictionary<string, ExtractedParameter> _parameters = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedParameter> ExtractedParameters => new List<ExtractedParameter>(_parameters.Values);

    private static List<(int Start, int Stop, bool IsComment)> GetExcludedRanges(string sql)
    {
        var ranges = new List<(int Start, int Stop, bool IsComment)>();
        try
        {
            var charStream = new ZeroCopyCaseInsensitiveStream(sql.AsMemory());
            var lexer = new SqlBaseLexer(charStream);
            lexer.RemoveErrorListeners();
            var tokens = new CommonTokenStream(lexer);
            tokens.Fill();
            foreach (var tok in tokens.GetTokens())
            {
                if (tok.Type == Antlr4.Runtime.TokenConstants.EOF) break;
                if (tok.Type == SqlBaseLexer.STRING ||
                    tok.Type == SqlBaseLexer.UNICODE_STRING ||
                    tok.Type == SqlBaseLexer.DOLLAR_STRING)
                {
                    ranges.Add((tok.StartIndex, tok.StopIndex, false));
                }
                else if (tok.Type == SqlBaseLexer.SIMPLE_COMMENT ||
                         tok.Type == SqlBaseLexer.BRACKETED_COMMENT)
                {
                    ranges.Add((tok.StartIndex, tok.StopIndex, true));
                }
            }
        }
        catch
        {
            // fallback
        }
        return ranges;
    }

    private static bool IsInRanges(int index, List<(int Start, int Stop, bool IsComment)> ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            if (index >= ranges[i].Start && index <= ranges[i].Stop)
            {
                return true;
            }
        }
        return false;
    }

    public static string StripComments(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        var ranges = GetExcludedRanges(sql);
        if (ranges.Count == 0)
        {
            return SqlCommentRegex.Replace(sql, string.Empty);
        }

        var sb = new System.Text.StringBuilder(sql.Length);
        int lastPos = 0;

        foreach (var (start, stop, isComment) in ranges)
        {
            if (isComment)
            {
                if (start > lastPos)
                {
                    sb.Append(sql, lastPos, start - lastPos);
                }
                sb.Append(' ');
                lastPos = stop + 1;
            }
        }

        if (lastPos < sql.Length)
        {
            sb.Append(sql, lastPos, sql.Length - lastPos);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Fast extraction of parameter tokens (@param and {{param}}) from raw SQL text, ignoring comments and string literals (SQ-16).
    /// </summary>
    public static IReadOnlyList<ExtractedParameter> ExtractTokens(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return [];
        }

        var ranges = GetExcludedRanges(rawSql);
        var results = new Dictionary<string, ExtractedParameter>(StringComparer.OrdinalIgnoreCase);
        var matches = ParameterRegex.Matches(rawSql);

        foreach (Match match in matches)
        {
            if (IsInRanges(match.Index, ranges))
            {
                continue;
            }

            string name = !string.IsNullOrEmpty(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;

            string token = match.Value;

            if (!results.ContainsKey(name))
            {
                results[name] = new ExtractedParameter(
                    Name: name,
                    Token: token,
                    FirstPosition: match.Index);
            }
        }

        return new List<ExtractedParameter>(results.Values);
    }

    /// <summary>
    /// Normalizes raw SQL containing @param or {{param}} into AST-compliant identifiers __param_{name},
    /// preserving string literals and stripping comments (SQ-16).
    /// </summary>
    public static string NormalizeForAst(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return string.Empty;
        }

        string cleanSql = StripComments(rawSql);
        string trimmed = cleanSql.Trim().TrimEnd(';');
        var ranges = GetExcludedRanges(trimmed);

        var matches = ParameterRegex.Matches(trimmed);
        if (matches.Count == 0)
        {
            return trimmed;
        }

        var sb = new System.Text.StringBuilder(trimmed.Length);
        int lastPos = 0;

        foreach (Match match in matches)
        {
            if (IsInRanges(match.Index, ranges))
            {
                continue;
            }

            if (match.Index > lastPos)
            {
                sb.Append(trimmed, lastPos, match.Index - lastPos);
            }

            string name = !string.IsNullOrEmpty(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;

            sb.Append(IsRowCountPosition(trimmed, match.Index) ? "?" : $"__param_{name}");
            lastPos = match.Index + match.Length;
        }

        if (lastPos < trimmed.Length)
        {
            sb.Append(trimmed, lastPos, trimmed.Length - lastPos);
        }

        return sb.ToString();
    }

    private static bool IsRowCountPosition(string sql, int parameterIndex)
    {
        int start = Math.Max(0, parameterIndex - 32);
        return RowCountContextRegex.IsMatch(sql[start..parameterIndex]);
    }

    /// <summary>
    /// Analyzes an AST statement and enriches extracted parameters with contextual target column and operator.
    /// </summary>
    public static IReadOnlyList<ExtractedParameter> AnalyzeAstParameters(
        SqlBaseParser.SingleStatementContext statementContext,
        IReadOnlyList<ExtractedParameter> initialParams)
    {
        ArgumentNullException.ThrowIfNull(statementContext);
        ArgumentNullException.ThrowIfNull(initialParams);

        var extractor = new SqlParameterExtractor();
        foreach (var p in initialParams)
        {
            extractor._parameters[p.Name] = p;
        }

        ParseTreeWalker.Default.Walk(extractor, statementContext);
        return extractor.ExtractedParameters;
    }

    public override void EnterComparison(SqlBaseParser.ComparisonContext context)
    {
        var op = context.comparisonOperator();
        string opText = op?.GetText() ?? "=";

        var leftCtx = context.value ?? (context.Parent as SqlBaseParser.PredicatedContext)?.valueExpression();
        var rightCtx = context.right ?? context.valueExpression();

        if (leftCtx == null || rightCtx == null) return;

        string leftText = leftCtx.GetText();
        string rightText = rightCtx.GetText();

        CheckAndAssociate(leftText, rightText, opText);
        CheckAndAssociate(rightText, leftText, opText);
    }

    private void CheckAndAssociate(string possibleColText, string possibleParamText, string opText)
    {
        if (possibleParamText.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
        {
            string paramName = possibleParamText["__param_".Length..];
            if (_parameters.TryGetValue(paramName, out var existing))
            {
                string normCol = SqlIdentifierHelper.NormalizeIdentifier(possibleColText);
                string? targetTable = null;
                string targetCol = normCol;

                int dotIdx = normCol.LastIndexOf('.');
                if (dotIdx > 0)
                {
                    targetTable = normCol[..dotIdx];
                    targetCol = normCol[(dotIdx + 1)..];
                }

                _parameters[paramName] = existing with
                {
                    TargetColumn = targetCol,
                    TargetTable = targetTable,
                    ComparisonOperator = opText
                };
            }
        }
    }
}
