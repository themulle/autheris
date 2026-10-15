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
            default:
                foreach (var child in node.EnumerateChildren()) stack.Push(child);
                break;
        }
    }

    /// <summary>
    /// CR-ADG-29: two visible CTE names that differ only in case bind to the same object on case-insensitive engines, so the
    /// gateway's scope decision and the database's name binding could diverge. Such scopes are rejected.
    /// </summary>
    private void EnsureCteNameIsUnambiguous(string cteKey)
    {
        foreach (string visible in _cteScopeStack.Peek())
        {
            if (!string.Equals(visible, cteKey, StringComparison.Ordinal) &&
                string.Equals(visible, cteKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("CTE names that differ only in case are ambiguous and are not permitted.");
            }
        }
    }

    private void EnsureRecursiveCteIsNotACatalogTable(SqlIdentifier name)
    {
        bool isTable = _typed.Catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier(name.Value, name.IsQuoted) })) != null;
        if (isTable)
        {
            throw new SecurityException("A recursive CTE must not have the name of a catalog table; the reference would be ambiguous.");
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
        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        if (_options.TablesWithMaskedColumns.Contains(normalizedTableName) ||
            _options.TablesWithMaskedColumns.Contains(simpleTableName))
        {
            return true;
        }

        var columns = _options.TableColumns(normalizedTableName);
        if (columns != null)
        {
            foreach (var col in columns)
            {
                if (_options.MaskedColumns.HasMask(normalizedTableName, col))
                    return true;
            }
        }

        return false;
    }

    public const string RedactedLiteralPlaceholder = "@p_redacted";
    public const int DefaultMaxAuditSqlLength = 4096;

    /// <summary>
    /// The audit text of a statement that cannot be redacted token by token (the lexer reported an error, so a literal may not have been
    /// recognised as one, for example an unterminated string). Nothing of the statement is kept.
    /// </summary>
    public const string FullyRedactedStatement = "@p_redacted_statement";

    /// <summary>
    /// AU-06: GDPR Art. 17 AST literal anonymization for SQL audit details (@p_redacted).
    /// Replaces all string and numeric literals with @p_redacted. The result carries no literal value, so a hash over it (M-A3: the
    /// keyed <c>originalSqlHash</c> that the gateway builds) can never leak or confirm a value; there is deliberately no hash over the raw text.
    /// </summary>
    public static string RedactSqlLiterals(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        string result;
        try
        {
            var charStream = new ZeroCopyCaseInsensitiveStream(sql.AsMemory());
            var lexer = new SqlBaseLexer(charStream);
            lexer.RemoveErrorListeners();
            var lexerErrors = new LexerErrorCounter();
            lexer.AddErrorListener(lexerErrors);
            var tokens = new CommonTokenStream(lexer);
            tokens.Fill();

            var tokenList = tokens.GetTokens();

            // L-3: an error of the lexer, or a character that it could not place (UNRECOGNIZED, such as the quote of an unterminated string
            // whose text then lexes as plain identifiers), means that a literal may be left in the text: redact the whole statement.
            if (lexerErrors.Count > 0 || tokenList.Any(static t => t.Type == SqlBaseLexer.UNRECOGNIZED))
            {
                return FullyRedactedStatement;
            }

            // SD-1: the text is built from the token indices (every token of the stream, hidden-channel whitespace and comments included),
            // never by editing the statement through a TokenStreamRewriter.
            var redacted = new System.Text.StringBuilder(sql.Length);
            for (int i = 0; i < tokenList.Count; i++)
            {
                var tok = tokenList[i];
                if (tok.Type == TokenConstants.EOF) break;

                if (tok.Type == SqlBaseLexer.STRING ||
                    tok.Type == SqlBaseLexer.UNICODE_STRING ||
                    tok.Type == SqlBaseLexer.DOLLAR_STRING ||
                    tok.Type == SqlBaseLexer.INTEGER_VALUE ||
                    tok.Type == SqlBaseLexer.DECIMAL_VALUE ||
                    tok.Type == SqlBaseLexer.DOUBLE_VALUE ||
                    tok.Type == SqlBaseLexer.BINARY_LITERAL)
                {
                    redacted.Append(RedactedLiteralPlaceholder);
                }
                else
                {
                    redacted.Append(tok.Text);
                }
            }

            result = redacted.ToString();
        }
        catch
        {
            result = FullyRedactedStatement;
        }

        return result;
    }

    private sealed class LexerErrorCounter : IAntlrErrorListener<int>
    {
        public int Count { get; private set; }

        public void SyntaxError(System.IO.TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) => Count++;
    }

    /// <summary>The audit text of an already redacted statement: cut to <paramref name="maxLength"/> characters.</summary>
    public static string TruncateForAudit(string redactedSql, int maxLength = DefaultMaxAuditSqlLength) =>
        redactedSql.Length > maxLength ? redactedSql[..maxLength] + "...[TRUNCATED]" : redactedSql;
}
