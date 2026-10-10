using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Ast.Visitors;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private static void PushChildren(SqlNode node, Stack<SqlNode> stack)
    {
        switch (node)
        {
            case ParenthesizedExpression p:
                stack.Push(p.Expression);
                break;
            case BinaryExpression b:
                stack.Push(b.Right);
                stack.Push(b.Left);
                break;
            case UnaryExpression u:
                stack.Push(u.Operand);
                break;
            case CastExpression cast:
                stack.Push(cast.Operand);
                break;
            case LikeExpression lk:
                if (lk.Escape != null) stack.Push(lk.Escape);
                stack.Push(lk.Pattern);
                stack.Push(lk.Operand);
                break;
            case IsDistinctFromExpression df:
                stack.Push(df.Right);
                stack.Push(df.Left);
                break;
            case BetweenExpression bt:
                stack.Push(bt.Upper);
                stack.Push(bt.Lower);
                stack.Push(bt.Operand);
                break;
            case InListExpression inL:
                foreach (var it in inL.Items) stack.Push(it);
                stack.Push(inL.Operand);
                break;
            case InSubqueryExpression inSq:
                stack.Push(inSq.Subquery);
                stack.Push(inSq.Operand);
                break;
            case ScalarSubqueryExpression sc:
                stack.Push(sc.Subquery);
                break;
            case ExistsExpression ex:
                stack.Push(ex.Subquery);
                break;
            case QuantifiedComparisonExpression qc:
                stack.Push(qc.Subquery);
                stack.Push(qc.Left);
                break;
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments) stack.Push(arg);
                if (fn.Filter != null) stack.Push(fn.Filter);
                if (fn.OrderWithin != null) stack.Push(fn.OrderWithin);
                if (fn.Window?.PartitionBy != null) foreach (var p in fn.Window.PartitionBy) stack.Push(p);
                if (fn.Window?.OrderBy != null) stack.Push(fn.Window.OrderBy);
                break;
            case CaseExpression cs:
                if (cs.ElseResult != null) stack.Push(cs.ElseResult);
                foreach (var w in cs.WhenClauses) { stack.Push(w.Result); stack.Push(w.Condition); }
                if (cs.Operand != null) stack.Push(cs.Operand);
                break;
            case RowValueExpression row:
                foreach (var el in row.Elements) stack.Push(el);
                break;
            case ArrayConstructorExpression arr:
                foreach (var el in arr.Elements) stack.Push(el);
                break;
            case SubscriptExpression sub:
                stack.Push(sub.Index);
                stack.Push(sub.Target);
                break;
            case ExtractExpression ext:
                stack.Push(ext.Source);
                break;
            case SubstringExpression sub:
                stack.Push(sub.Source);
                stack.Push(sub.Start);
                if (sub.Length != null) stack.Push(sub.Length);
                break;
            case TrimExpression trim:
                stack.Push(trim.Source);
                if (trim.Characters != null) stack.Push(trim.Characters);
                break;
            case DateFunctionExpression date:
                stack.Push(date.Source);
                break;
            case PositionExpression pos:
                stack.Push(pos.Needle);
                stack.Push(pos.Haystack);
                break;
            case SelectStatement s:
                if (s.With != null) { foreach (var cte in s.With.Ctes) stack.Push(cte.Query); }
                stack.Push(s.Body);
                if (s.OrderBy != null) stack.Push(s.OrderBy);
                if (s.Pagination != null)
                {
                    if (s.Pagination.Offset != null) stack.Push(s.Pagination.Offset);
                    if (s.Pagination.Limit != null) stack.Push(s.Pagination.Limit);
                }
                break;
            case QuerySpecification qs:
                foreach (var p in qs.Projections) stack.Push(p);
                if (qs.From != null) stack.Push(qs.From);
                if (qs.Where != null) stack.Push(qs.Where);
                if (qs.GroupBy != null) stack.Push(qs.GroupBy);
                if (qs.Having != null) stack.Push(qs.Having);
                break;
            case SetOperationQuery so:
                stack.Push(so.Left);
                stack.Push(so.Right);
                break;
            case ValuesQueryBody vq:
                foreach (var r in vq.Rows) stack.Push(r);
                break;
            case JoinedTableSource jt:
                stack.Push(jt.Left);
                stack.Push(jt.Right);
                if (jt.Condition != null) stack.Push(jt.Condition);
                break;
            case SubqueryTableSource st:
                stack.Push(st.Subquery);
                break;
            case LateralTableSource lt:
                stack.Push(lt.Subquery);
                break;
            case OnJoinCondition on:
                stack.Push(on.Predicate);
                break;
            case UsingJoinCondition:
                break;
            case GroupByClause gb:
                foreach (var g in gb.GroupingExpressions) stack.Push(g);
                if (gb.AdvancedElements != null)
                {
                    foreach (var adv in gb.AdvancedElements)
                    {
                        foreach (var set in adv.Sets)
                        {
                            foreach (var g in set) stack.Push(g);
                        }
                    }
                }
                break;
            case ColumnSelectItem csi:
                stack.Push(csi.Expression);
                break;
            case OrderByClause ob:
                foreach (var el in ob.Elements) stack.Push(el.Expression);
                break;
        }
    }

    private bool IsCte(SqlQualifiedName name)
    {
        if (!name.IsSimple) return false;
        string key = SqlIdentifierHelper.FoldIdentifierForScope(name.Parts[0]);
        return _cteScopeStack.Peek().Contains(key);
    }

    private bool HasMaskingForTable(string normalizedTableName)
    {
        if (_options.ColumnMaskingProvider == null) return false;

        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        if (_options.TablesWithMaskedColumns.Contains(normalizedTableName) ||
            _options.TablesWithMaskedColumns.Contains(simpleTableName))
        {
            return true;
        }

        if (_options.TableColumnsProvider != null)
        {
            var columns = _options.TableColumnsProvider(normalizedTableName);
            if (columns != null)
            {
                foreach (var col in columns)
                {
                    if (_options.ColumnMaskingProvider.HasMask(normalizedTableName, col))
                        return true;
                }
            }
        }

        return false;
    }

    public const string RedactedLiteralPlaceholder = "@p_redacted";
    public const int DefaultMaxAuditSqlLength = 4096;

    private static readonly Regex FallbackStringLiteralRegex = new(
        @"'([^']|'')*'",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex FallbackNumericLiteralRegex = new(
        @"(?<=[=<>!,\s(+\-*/%])\d+(\.\d+)?(?=[=<>!,\s);+\-*/%]|$)|\b\d+(\.\d+)?\b",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// AU-06: DSGVO Art. 17 AST-Literal-Anonymisierung für SQL-Audit-Details (@p_redacted).
    /// Ersetzt alle String- und numerischen Literale durch @p_redacted und liefert den SHA-256 Hash des Original-SQL.
    /// </summary>
    public static (string RedactedSql, string OriginalSqlHash) AnonymizeSqlForAudit(string? sql, int maxLength = DefaultMaxAuditSqlLength)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return (string.Empty, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        string result;
        try
        {
            var charStream = new ZeroCopyCaseInsensitiveStream(sql.AsMemory());
            var lexer = new SqlBaseLexer(charStream);
            lexer.RemoveErrorListeners();
            var tokens = new CommonTokenStream(lexer);
            tokens.Fill();

            var rewriter = new TokenStreamRewriter(tokens);
            var tokenList = tokens.GetTokens();

            for (int i = 0; i < tokenList.Count; i++)
            {
                var tok = tokenList[i];
                if (tok.Type == TokenConstants.EOF) break;

                if (tok.Type == SqlBaseLexer.STRING ||
                    tok.Type == SqlBaseLexer.UNICODE_STRING ||
                    tok.Type == SqlBaseLexer.DOLLAR_STRING ||
                    tok.Type == SqlBaseLexer.INTEGER_VALUE ||
                    tok.Type == SqlBaseLexer.DECIMAL_VALUE)
                {
                    rewriter.Replace(tok.TokenIndex, RedactedLiteralPlaceholder);
                }
            }

            result = rewriter.GetText();
        }
        catch
        {
            var s = FallbackStringLiteralRegex.Replace(sql, RedactedLiteralPlaceholder);
            result = FallbackNumericLiteralRegex.Replace(s, RedactedLiteralPlaceholder);
        }

        if (result.Length > maxLength)
        {
            result = result[..maxLength] + "...[TRUNCATED]";
        }

        return (result, hash);
    }
}
