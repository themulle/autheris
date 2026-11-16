namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    public override SqlNode VisitLikeExpression(LikeExpression node)
    {
        var visited = (LikeExpression)base.VisitLikeExpression(node);
        return LikePatternEscaper.NormalizeUserLike(visited, _options.TargetDialect, _options.MaxQueryLength, _options.GatewayLikePatternParameters);
    }

    public override SqlNode VisitExistsExpression(ExistsExpression node) =>
        WithScope(SecurityScope.ExistsSubquery, () => base.VisitExistsExpression(node));

    public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node) =>
        WithScope(SecurityScope.InSubquery, () => base.VisitInSubqueryExpression(node));

    public override SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node) =>
        WithScope(SecurityScope.ScalarSubquery, () => base.VisitScalarSubqueryExpression(node));

    private static bool ContainsColumnReference(Expression? root)
    {
        if (root == null) return false;

        var stack = new Stack<Expression>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case ColumnReference:
                    return true;
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
                case LikeExpression l:
                    if (l.Escape != null) stack.Push(l.Escape);
                    stack.Push(l.Pattern);
                    stack.Push(l.Operand);
                    break;
                case InListExpression inList:
                    foreach (var item in inList.Items) stack.Push(item);
                    stack.Push(inList.Operand);
                    break;
                case InSubqueryExpression inSq:
                    if (ContainsColumnReferenceInQuery(inSq.Subquery)) return true;
                    stack.Push(inSq.Operand);
                    break;
                case ExistsExpression ex:
                    if (ContainsColumnReferenceInQuery(ex.Subquery)) return true;
                    break;
                case ScalarSubqueryExpression sc:
                    if (ContainsColumnReferenceInQuery(sc.Subquery)) return true;
                    break;
                case BetweenExpression between:
                    stack.Push(between.Upper);
                    stack.Push(between.Lower);
                    stack.Push(between.Operand);
                    break;
                case CaseExpression caseExpr:
                    if (caseExpr.ElseResult != null) stack.Push(caseExpr.ElseResult);
                    foreach (var w in caseExpr.WhenClauses)
                    {
                        stack.Push(w.Result);
                        stack.Push(w.Condition);
                    }
                    if (caseExpr.Operand != null) stack.Push(caseExpr.Operand);
                    break;
                case FunctionCallExpression func:
                    foreach (var arg in func.Arguments) stack.Push(arg);
                    break;
                case SubstringExpression sub:
                    if (sub.Length != null) stack.Push(sub.Length);
                    stack.Push(sub.Start);
                    stack.Push(sub.Source);
                    break;
                case TrimExpression trim:
                    if (trim.Characters != null) stack.Push(trim.Characters);
                    stack.Push(trim.Source);
                    break;
                case PositionExpression pos:
                    stack.Push(pos.Haystack);
                    stack.Push(pos.Needle);
                    break;
                case CastExpression cast:
                    stack.Push(cast.Operand);
                    break;
                case DateFunctionExpression dateFunc:
                    stack.Push(dateFunc.Source);
                    break;
                case ExtractExpression extract:
                    stack.Push(extract.Source);
                    break;
                case IsDistinctFromExpression distinct:
                    stack.Push(distinct.Right);
                    stack.Push(distinct.Left);
                    break;
                case QuantifiedComparisonExpression quant:
                    if (ContainsColumnReferenceInQuery(quant.Subquery)) return true;
                    stack.Push(quant.Left);
                    break;
            }
        }

        return false;
    }

    private static bool ContainsColumnReferenceInQuery(SelectStatement subquery)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(subquery);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference or WildcardSelectItem or UsingJoinCondition)
            {
                return true;
            }
            PushChildren(node, stack);
        }

        return false;
    }

    private static bool TryCompareNumericLiterals(LiteralExpression left, LiteralExpression right, out int comparison) =>
        LiteralComparison.TryCompareNumeric(left, right, out comparison);

    /// <summary><c>x IS NULL OR x IS NOT NULL</c> (either order, same column): true for every row.</summary>
    private static bool IsNullComplement(Expression left, Expression right)
    {
        static Expression Unwrap(Expression e)
        {
            while (e is ParenthesizedExpression p) e = p.Expression;
            return e;
        }

        return Unwrap(left) is UnaryExpression { Operand: ColumnReference a } l &&
               Unwrap(right) is UnaryExpression { Operand: ColumnReference b } r &&
               l.Operator != r.Operator &&
               l.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull &&
               r.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull &&
               a.Name.NormalizedName.Equals(b.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Constant-false forms (<c>false</c>, <c>1 = 0</c>, <c>NOT true</c>, ...), so that <c>NOT (...)</c> of them is a tautology.</summary>
    private static bool IsTriviallyFalse(Expression? expr)
    {
        switch (expr)
        {
            case ParenthesizedExpression p:
                return IsTriviallyFalse(p.Expression);
            case LiteralExpression { Type: LiteralType.Boolean } lit:
                return false.Equals(lit.Value);
            case UnaryExpression { Operator: UnaryOperator.Not } not:
                return IsTriviallyTrue(not.Operand);
            case BinaryExpression { Operator: BinaryOperator.And } and:
            {
                Expression cur = and;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.And } a)
                {
                    rights.Add(a.Right);
                    cur = a.Left;
                }

                if (IsTriviallyFalse(cur)) return true;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (IsTriviallyFalse(rights[i])) return true;
                }

                return false;
            }
            case BinaryExpression { Operator: BinaryOperator.Or } or:
            {
                Expression cur = or;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.Or } o)
                {
                    rights.Add(o.Right);
                    cur = o.Left;
                }

                if (!IsTriviallyFalse(cur)) return false;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (!IsTriviallyFalse(rights[i])) return false;
                }

                return true;
            }
            case BinaryExpression { Left: LiteralExpression l, Right: LiteralExpression r } cmp
                when TryCompareNumericLiterals(l, r, out int c):
                return cmp.Operator switch
                {
                    BinaryOperator.Equal => c != 0,
                    BinaryOperator.NotEqual => c == 0,
                    BinaryOperator.LessThan => c >= 0,
                    BinaryOperator.LessThanOrEqual => c > 0,
                    BinaryOperator.GreaterThan => c <= 0,
                    BinaryOperator.GreaterThanOrEqual => c < 0,
                    _ => false
                };
            default:
                return false;
        }
    }

    private static bool IsTriviallyTrue(Expression? expr)
    {
        if (expr == null) return false;

        switch (expr)
        {
            case ParenthesizedExpression p:
                return IsTriviallyTrue(p.Expression);
            case LiteralExpression lit when lit.Type == LiteralType.Boolean:
                return true.Equals(lit.Value);
            case BinaryExpression b when b.Operator == BinaryOperator.Or:
            {
                if (b.Left is not BinaryExpression { Operator: BinaryOperator.Or })
                {
                    return IsTriviallyTrue(b.Left) || IsTriviallyTrue(b.Right) || IsNullComplement(b.Left, b.Right);
                }

                Expression cur = b;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.Or } o)
                {
                    rights.Add(o.Right);
                    cur = o.Left;
                }

                if (IsTriviallyTrue(cur)) return true;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (IsTriviallyTrue(rights[i])) return true;
                    if (i == rights.Count - 1 && IsNullComplement(cur, rights[i])) return true;
                }

                return false;
            }
            case BinaryExpression b when b.Operator == BinaryOperator.And:
            {
                Expression cur = b;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.And } a)
                {
                    rights.Add(a.Right);
                    cur = a.Left;
                }

                if (!IsTriviallyTrue(cur)) return false;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (!IsTriviallyTrue(rights[i])) return false;
                }

                return true;
            }
            case BinaryExpression b when b.Operator == BinaryOperator.Equal:
                if (b.Left is LiteralExpression el1 && b.Right is LiteralExpression el2)
                {
                    if (TryCompareNumericLiterals(el1, el2, out int cmpEq))
                        return cmpEq == 0;
                    return Equals(el1.Value?.ToString(), el2.Value?.ToString());
                }
                if (b.Left is ColumnReference ec1 && b.Right is ColumnReference ec2)
                {
                    return ec1.Name.NormalizedName.Equals(ec2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.NotEqual:
                if (b.Left is LiteralExpression nl1 && b.Right is LiteralExpression nl2)
                {
                    if (TryCompareNumericLiterals(nl1, nl2, out int cmpNeq))
                        return cmpNeq != 0;
                    return !Equals(nl1.Value?.ToString(), nl2.Value?.ToString());
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThan:
                if (b.Left is LiteralExpression ltl && b.Right is LiteralExpression ltr &&
                    TryCompareNumericLiterals(ltl, ltr, out int cmpLt))
                {
                    return cmpLt < 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThanOrEqual:
                if (b.Left is LiteralExpression le1 && b.Right is LiteralExpression le2)
                {
                    if (TryCompareNumericLiterals(le1, le2, out int cmpLe))
                        return cmpLe <= 0;
                    return Equals(le1.Value?.ToString(), le2.Value?.ToString());
                }
                if (b.Left is ColumnReference leCol1 && b.Right is ColumnReference leCol2)
                {
                    return leCol1.Name.NormalizedName.Equals(leCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThan:
                if (b.Left is LiteralExpression gtl && b.Right is LiteralExpression gtr &&
                    TryCompareNumericLiterals(gtl, gtr, out int cmpGt))
                {
                    return cmpGt > 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThanOrEqual:
                if (b.Left is LiteralExpression ge1 && b.Right is LiteralExpression ge2)
                {
                    if (TryCompareNumericLiterals(ge1, ge2, out int cmpGe))
                        return cmpGe >= 0;
                    return Equals(ge1.Value?.ToString(), ge2.Value?.ToString());
                }
                if (b.Left is ColumnReference geCol1 && b.Right is ColumnReference geCol2)
                {
                    return geCol1.Name.NormalizedName.Equals(geCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BetweenExpression between:
                if (between.Operand is ColumnReference opCol &&
                    between.Lower is ColumnReference lowCol &&
                    between.Upper is ColumnReference upCol)
                {
                    if (opCol.Name.NormalizedName.Equals(lowCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase) &&
                        opCol.Name.NormalizedName.Equals(upCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            case InListExpression inList:
                if (inList.Operand is ColumnReference inCol &&
                    inList.Items.Any(item => item is ColumnReference itemCol &&
                                            itemCol.Name.NormalizedName.Equals(inCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                return false;
            case UnaryExpression u when u.Operator == UnaryOperator.Not:
                return IsTriviallyFalse(u.Operand);
            case UnaryExpression u when u.Operator == UnaryOperator.IsNotNull:
                return u.Operand is LiteralExpression constLit && constLit.Type != LiteralType.Null;
            default:
                return false;
        }
    }

    // ---- injected predicates ----

    private List<Expression> BuildTargetPredicates(TableCatalogEntry entry, SecurityScope scope, SqlIdentifier? qualifier)
    {
        var typed = _typed;
        var tid = entry.Identity;
        var predicates = new List<Expression>(2);
        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant != null)
        {
            predicates.Add(new SecurityPredicateExpression(Qualify(tenant, qualifier), new SecurityPredicateId(tid.ToString(), 0), scope));
        }

        if (typed.RowFilters.ShouldApplyPolicy(tid))
        {
            var expression = BuildPolicyExpression(entry);
            predicates.Add(new SecurityPredicateExpression(Qualify(expression, qualifier), new SecurityPredicateId(tid.ToString(), 1), scope));
        }

        return predicates;
    }

    /// <summary>The row policy of a DML target as an expression over the target's columns (parameters registered); a correlated policy is rejected.</summary>
    private Expression BuildPolicyExpression(TableCatalogEntry entry)
    {
        var typed = _typed;
        var predicate = typed.RowFilters.GetPredicate(entry.Identity);
        typed.AddValues(predicate.Parameters);
        var expression = (Expression)new PolicySubqueryTenantRewriter(typed).Visit(predicate.Expression);
        expression = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(expression);
        bool referencesTarget = AstReflection.Collect<ColumnReference>(expression).Any(c =>
            c.Name.Parts.Count >= 2 && string.Equals(c.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
        if (referencesTarget)
        {
            // 06 item 2: a correlated row filter cannot be bound to a DML target (nor to the inserted values).
            throw new SecurityException("Correlated row filters are not supported for INSERT, UPDATE, DELETE and MERGE statements.");
        }

        return expression;
    }

    private static Expression Qualify(Expression predicate, SqlIdentifier? qualifier) =>
        qualifier is null ? predicate : (Expression)new TargetColumnQualifier { Qualifier = qualifier }.Visit(predicate);

    /// <summary>The user's part first (parenthesized, so an OR cannot absorb the injected predicates), then every injected predicate.</summary>
    private static Expression? AndInjected(Expression? userPart, IReadOnlyList<Expression> injected)
    {
        Expression? result = userPart is null ? null : new ParenthesizedExpression(userPart);
        foreach (var predicate in injected)
        {
            result = result is null ? predicate : new BinaryExpression(result, BinaryOperator.And, predicate);
        }

        return result;
    }

    /// <summary>Qualifies the unqualified column references of a predicate that belong to the target; nested SELECTs are left alone.</summary>
    private sealed class TargetColumnQualifier : SqlAstRewriter
    {
        public SqlIdentifier Qualifier { get; init; } = new("t");

        public override SqlNode VisitSelectStatement(SelectStatement node) => node;

        public override SqlNode VisitColumnReference(ColumnReference node) =>
            node.Name.Parts.Count == 1
                ? new ColumnReference(new SqlQualifiedName(new[] { Qualifier, node.Name.Parts[0] }))
                : node;
    }
}
