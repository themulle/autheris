namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Transforms an ANTLR4 ParseTree (from SqlBase.g4) into the strongly-typed immutable AST Intermediate Representation.
/// Applies fail-closed statement filtering, depth guards (Anti-DoS), and function/table policies.
/// </summary>
public sealed class SqlAstBuilder : SqlBaseBaseVisitor<SqlNode>
{
    private readonly AstBuilderOptions _options;
    private int _currentDepth;

    public SqlAstBuilder(AstBuilderOptions? options = null)
    {
        _options = options ?? new AstBuilderOptions();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ScopeGuard EnterScope()
    {
        if (++_currentDepth > _options.MaxAllowedAstDepth)
        {
            throw new SecurityException(
                $"SQL query exceeded the maximum allowable AST nesting depth of {_options.MaxAllowedAstDepth}.");
        }
        return new ScopeGuard(this);
    }

    private readonly ref struct ScopeGuard
    {
        private readonly SqlAstBuilder _builder;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ScopeGuard(SqlAstBuilder builder)
        {
            _builder = builder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            _builder._currentDepth--;
        }
    }

    /// <summary>
    /// Builds a typed SqlStatement from a singleStatement ParseTree context.
    /// </summary>
    public SqlStatement BuildStatement(SqlBaseParser.SingleStatementContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureParseTreeDepthWithinLimit(context, 3_000);
        _currentDepth = 0;
        return (SqlStatement)Visit(context);
    }

    /// <summary>
    /// Builds a typed Expression from an expression ParseTree context.
    /// </summary>
    public Expression BuildExpression(SqlBaseParser.ExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _currentDepth = 0;
        return (Expression)Visit(context);
    }

    /// <summary>
    /// Builds a typed Expression from a standaloneExpression ParseTree context.
    /// </summary>
    public Expression BuildStandaloneExpression(SqlBaseParser.StandaloneExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _currentDepth = 0;
        return (Expression)Visit(context.expression());
    }

    private static void EnsureParseTreeDepthWithinLimit(IParseTree root, int maxDepth)
    {
        if (maxDepth <= 0) return;
        var stack = new Stack<(IParseTree node, int depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                throw new SecurityException($"SQL parse tree depth exceeds the maximum allowed limit of {maxDepth}.");
            }
            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child.ChildCount > 0)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// Wunsch 4: every grammar rule without an explicit visitor is rejected. The ANTLR default (visit all children and
    /// return the last result) silently dropped constructs such as ROLLUP, FILTER or window frames, or returned null.
    /// </summary>
    public override SqlNode VisitChildren(IRuleNode node)
    {
        string rule = node.RuleContext.GetType().Name;
        if (rule.EndsWith("Context", StringComparison.Ordinal))
        {
            rule = rule[..^"Context".Length];
        }

        throw Unsupported($"'{rule}'");
    }

    private static AstBuildException Unsupported(string construct) =>
        new($"SQL construct {construct} is not supported by the AST compiler.");

    public override SqlNode VisitSingleStatement(SqlBaseParser.SingleStatementContext context)
    {
        using var _ = EnterScope();
        var stmt = context.statement();
        if (stmt == null)
        {
            throw new AstBuildException("Statement context is empty.");
        }

        if (_options.EnforceReadOnlyQueries)
        {
            if (stmt is not SqlBaseParser.StatementDefaultContext)
            {
                throw new SecurityException($"RLS rewriter only allows read-only SELECT statements. Rejected: {stmt.GetType().Name}");
            }
        }
        else
        {
            if (stmt is not SqlBaseParser.StatementDefaultContext &&
                stmt is not SqlBaseParser.DeleteContext &&
                stmt is not SqlBaseParser.UpdateContext &&
                stmt is not SqlBaseParser.InsertIntoContext)
            {
                throw new SecurityException($"Unsupported or unsafe statement type: {stmt.GetType().Name}");
            }
        }

        return Visit(stmt);
    }

    public override SqlNode VisitStatementDefault(SqlBaseParser.StatementDefaultContext context)
    {
        return Visit(context.rootQueryWithSession());
    }

    public override SqlNode VisitRootQueryWithSession(SqlBaseParser.RootQueryWithSessionContext context)
    {
        var properties = context.sessionProperty();
        if (properties != null && properties.Length > 0)
        {
            foreach (var property in properties)
            {
                string name = SqlIdentifierHelper.NormalizeQualifiedName(property.qualifiedName());
                if (_options.AllowedSessionProperties == null ||
                    !SqlFunctionPolicy.ContainsIgnoreCase(_options.AllowedSessionProperties, name))
                {
                    throw new SecurityException($"WITH SESSION property '{name}' is not permitted.");
                }
            }
        }

        return Visit(context.rootQuery());
    }

    public override SqlNode VisitRootQuery(SqlBaseParser.RootQueryContext context)
    {
        var functions = context.functionSpecification();
        if (functions != null && functions.Length > 0 && !_options.AllowInlineFunctionDefinitions)
        {
            throw new SecurityException("Inline function definitions (WITH FUNCTION) are not permitted.");
        }

        return Visit(context.query());
    }

    public override SqlNode VisitQuery(SqlBaseParser.QueryContext context)
    {
        using var _ = EnterScope();
        WithClause? withClause = null;
        if (context.with() != null)
        {
            bool isRecursive = context.with().RECURSIVE() != null;
            var ctes = new List<CommonTableExpression>();
            foreach (var namedQuery in context.with().namedQuery())
            {
                var name = ToSqlIdentifier(namedQuery.name);
                IReadOnlyList<SqlIdentifier>? colAliases = null;
                if (namedQuery.columnAliases()?.identifier() != null)
                {
                    colAliases = namedQuery.columnAliases().identifier().Select(ToSqlIdentifier).ToList();
                }

                var cteQuery = (SelectStatement)Visit(namedQuery.query());
                ctes.Add(new CommonTableExpression(name, colAliases, cteQuery));
            }
            withClause = new WithClause(isRecursive, ctes);
        }

        return BuildQueryNoWith(context.queryNoWith(), withClause);
    }

    /// <summary>ORDER BY of a query, a window (Wunsch 4: was dropped) or inside an aggregate.</summary>
    private OrderByClause BuildOrderBy(SqlBaseParser.OrderByContext context)
    {
        var sortItems = context.sortItem();
        var elements = new List<OrderByElement>(sortItems.Length);
        foreach (var item in sortItems)
        {
            var expr = (Expression)Visit(item.expression());
            var dir = item.ordering?.Type == SqlBaseLexer.DESC ? SortDirection.Descending : SortDirection.Ascending;
            var nullOrder = NullOrdering.Default;
            if (item.nullOrdering?.Type == SqlBaseLexer.FIRST) nullOrder = NullOrdering.First;
            else if (item.nullOrdering?.Type == SqlBaseLexer.LAST) nullOrder = NullOrdering.Last;

            elements.Add(new OrderByElement(expr, dir, nullOrder));
        }

        return new OrderByClause(elements);
    }

    /// <summary>
    /// Wunsch 4: ROWS/RANGE frames with UNBOUNDED, CURRENT ROW or a non-negative integer literal offset. GROUPS (no SQL
    /// Server/Oracle support) and row pattern recognition are rejected.
    /// </summary>
    private WindowFrame BuildWindowFrame(SqlBaseParser.WindowFrameContext frame)
    {
        if (frame.measureDefinition().Length > 0 || frame.frameExclusion() != null || frame.skipTo() != null ||
            frame.INITIAL() != null || frame.SEEK() != null || frame.rowPattern() != null ||
            frame.subsetDefinition().Length > 0 || frame.variableDefinition().Length > 0)
        {
            throw Unsupported("row pattern recognition / frame exclusion in windows");
        }

        var extent = frame.frameExtent();
        var type = extent.frameType.Type switch
        {
            SqlBaseLexer.ROWS => WindowFrameType.Rows,
            SqlBaseLexer.RANGE => WindowFrameType.Range,
            _ => throw Unsupported("GROUPS window frames")
        };

        return new WindowFrame(type, BuildFrameBound(extent.start), extent.end != null ? BuildFrameBound(extent.end) : null);
    }

    private FrameBound BuildFrameBound(SqlBaseParser.FrameBoundContext bound)
    {
        switch (bound)
        {
            case SqlBaseParser.UnboundedFrameContext u:
                return new FrameBound(u.boundType.Type == SqlBaseLexer.PRECEDING ? FrameBoundKind.UnboundedPreceding : FrameBoundKind.UnboundedFollowing);
            case SqlBaseParser.CurrentRowBoundContext:
                return new FrameBound(FrameBoundKind.CurrentRow);
            case SqlBaseParser.BoundedFrameContext b:
                if (Visit(b.expression()) is not LiteralExpression { Type: LiteralType.Integer, Value: long offset } || offset < 0)
                {
                    throw Unsupported("window frame offsets other than non-negative integer literals");
                }

                return new FrameBound(b.boundType.Type == SqlBaseLexer.PRECEDING ? FrameBoundKind.Preceding : FrameBoundKind.Following, offset);
            default:
                throw Unsupported("window frame bound");
        }
    }

    private SelectStatement BuildQueryNoWith(SqlBaseParser.QueryNoWithContext context, WithClause? withClause)
    {
        var body = (QueryBody)Visit(context.queryTerm());

        OrderByClause? orderBy = context.orderBy() != null ? BuildOrderBy(context.orderBy()) : null;

        PaginationClause? pagination = null;
        Expression? offset = null;
        Expression? limit = null;

        if (context.offset != null)
        {
            offset = ParseRowCount(context.offset);
        }

        if (context.limit != null)
        {
            if (context.limit.rowCount() != null)
            {
                limit = ParseRowCount(context.limit.rowCount());
            }
        }
        else if (context.FETCH() != null)
        {
            if (context.fetchFirst != null)
            {
                limit = ParseRowCount(context.fetchFirst);
            }
            else
            {
                limit = new LiteralExpression(1L, LiteralType.Integer);
            }
        }

        if (offset != null || limit != null)
        {
            pagination = new PaginationClause(offset, limit);
        }

        return new SelectStatement(withClause, body, orderBy, pagination);
    }

    private static Expression ParseRowCount(SqlBaseParser.RowCountContext context)
    {
        if (context.INTEGER_VALUE() != null)
        {
            long val = long.Parse(context.INTEGER_VALUE().GetText(), CultureInfo.InvariantCulture);
            return new LiteralExpression(val, LiteralType.Integer);
        }
        return new ParameterReference("?");
    }

    public override SqlNode VisitQueryTermDefault(SqlBaseParser.QueryTermDefaultContext context)
    {
        return Visit(context.queryPrimary());
    }

    public override SqlNode VisitSetOperation(SqlBaseParser.SetOperationContext context)
    {
        using var _ = EnterScope();
        var left = (QueryBody)Visit(context.left);
        var right = (QueryBody)Visit(context.right);

        var op = SetOperator.Union;
        if (context.@operator.Type == SqlBaseLexer.INTERSECT) op = SetOperator.Intersect;
        else if (context.@operator.Type == SqlBaseLexer.EXCEPT) op = SetOperator.Except;

        bool distinct = context.setQuantifier()?.ALL() == null;
        return new SetOperationQuery(left, op, distinct, right);
    }

    public override SqlNode VisitQueryPrimaryDefault(SqlBaseParser.QueryPrimaryDefaultContext context)
    {
        return Visit(context.querySpecification());
    }

    public override SqlNode VisitTable(SqlBaseParser.TableContext context)
    {
        using var _ = EnterScope();
        return new TableQueryBody(ToSqlQualifiedName(context.qualifiedName()));
    }

    public override SqlNode VisitInlineTable(SqlBaseParser.InlineTableContext context)
    {
        using var _ = EnterScope();
        var exprs = context.expression();
        var rows = new List<RowValueExpression>(exprs.Length);
        foreach (var expr in exprs)
        {
            var visited = (Expression)Visit(expr);
            if (visited is RowValueExpression row)
            {
                rows.Add(row);
            }
            else
            {
                rows.Add(new RowValueExpression(new[] { visited }));
            }
        }
        return new ValuesQueryBody(rows);
    }

    public override SqlNode VisitSubquery(SqlBaseParser.SubqueryContext context)
    {
        using var _ = EnterScope();
        return BuildQueryNoWith(context.queryNoWith(), null);
    }

    public override SqlNode VisitQuerySpecification(SqlBaseParser.QuerySpecificationContext context)
    {
        using var _ = EnterScope();
        bool distinct = context.setQuantifier()?.DISTINCT() != null;

        var selectItems = context.selectItem();
        var projections = new List<SelectItem>(selectItems.Length);
        foreach (var item in selectItems)
        {
            projections.Add((SelectItem)Visit(item));
        }

        TableSource? fromSource = null;
        var relations = context.relation();
        if (relations != null && relations.Length > 0)
        {
            fromSource = (TableSource)Visit(relations[0]);
            for (int i = 1; i < relations.Length; i++)
            {
                var nextRel = (TableSource)Visit(relations[i]);
                fromSource = new JoinedTableSource(fromSource, JoinType.Cross, nextRel, null);
            }
        }

        Expression? where = context.where != null ? (Expression)Visit(context.where) : null;

        if (context.windowDefinition() is { Length: > 0 })
        {
            throw Unsupported("WINDOW (named window definitions)");
        }

        GroupByClause? groupBy = null;
        if (context.groupBy() != null)
        {
            if (context.groupBy().setQuantifier()?.DISTINCT() != null)
            {
                throw Unsupported("GROUP BY DISTINCT");
            }

            var groupingElements = context.groupBy().groupingElement();
            var groupingExpressions = new List<Expression>();
            foreach (var ge in groupingElements)
            {
                if (ge is not SqlBaseParser.SingleGroupingSetContext sgs)
                {
                    throw Unsupported($"GROUP BY {ge.GetChild(0).GetText().ToUpperInvariant()}");
                }

                var exprs = sgs.groupingSet().expression();
                if (exprs != null)
                {
                    foreach (var e in exprs)
                    {
                        groupingExpressions.Add((Expression)Visit(e));
                    }
                }
            }
            groupBy = new GroupByClause(groupingExpressions);
        }

        Expression? having = context.having != null ? (Expression)Visit(context.having) : null;

        return new QuerySpecification(distinct, projections, fromSource, where, groupBy, having);
    }

    public override SqlNode VisitSelectSingle(SqlBaseParser.SelectSingleContext context)
    {
        using var _ = EnterScope();
        var expr = (Expression)Visit(context.expression());
        SqlIdentifier? alias = context.identifier() != null ? ToSqlIdentifier(context.identifier()) : null;
        return new ColumnSelectItem(expr, alias);
    }

    public override SqlNode VisitSelectAll(SqlBaseParser.SelectAllContext context)
    {
        using var _ = EnterScope();
        SqlQualifiedName? qualifier = null;
        if (context.primaryExpression() != null)
        {
            var expr = (Expression)Visit(context.primaryExpression());
            if (expr is ColumnReference colRef)
            {
                qualifier = colRef.Name;
            }
        }
        return new WildcardSelectItem(qualifier);
    }

    public override SqlNode VisitRelationDefault(SqlBaseParser.RelationDefaultContext context)
    {
        return Visit(context.sampledRelation());
    }

    public override SqlNode VisitSampledRelation(SqlBaseParser.SampledRelationContext context)
    {
        return Visit(context.pivot().patternRecognition().aliasedRelation());
    }

    public override SqlNode VisitAliasedRelation(SqlBaseParser.AliasedRelationContext context)
    {
        using var _ = EnterScope();
        var primary = context.relationPrimary();
        var source = (TableSource)Visit(primary);

        SqlIdentifier? alias = context.identifier() != null ? ToSqlIdentifier(context.identifier()) : null;
        IReadOnlyList<SqlIdentifier>? colAliases = null;
        if (context.columnAliases()?.identifier() != null)
        {
            colAliases = context.columnAliases().identifier().Select(ToSqlIdentifier).ToList();
        }

        if (alias != null)
        {
            source = source switch
            {
                NamedTableSource n => n with { Alias = alias },
                SubqueryTableSource s => s with { Alias = alias, ColumnAliases = colAliases ?? s.ColumnAliases },
                LateralTableSource l => l with { Alias = alias, ColumnAliases = colAliases ?? l.ColumnAliases },
                _ => source
            };
        }

        return source;
    }

    public override SqlNode VisitTableName(SqlBaseParser.TableNameContext context)
    {
        using var _ = EnterScope();
        if (context.queryPeriod() != null && _options.RejectTimeTravelQueries)
        {
            throw new SecurityException("Time-travel queries (FOR TIMESTAMP/VERSION AS OF) are not permitted.");
        }

        var qualifiedName = ToSqlQualifiedName(context.qualifiedName());
        return new NamedTableSource(qualifiedName, null);
    }

    public override SqlNode VisitSubqueryRelation(SqlBaseParser.SubqueryRelationContext context)
    {
        using var _ = EnterScope();
        var subquery = (SelectStatement)Visit(context.query());
        return new SubqueryTableSource(subquery, new SqlIdentifier("subquery"), null);
    }

    public override SqlNode VisitLateral(SqlBaseParser.LateralContext context)
    {
        using var _ = EnterScope();
        var subquery = (SelectStatement)Visit(context.query());
        return new LateralTableSource(subquery, new SqlIdentifier("lateral_table"), null);
    }

    public override SqlNode VisitParenthesizedRelation(SqlBaseParser.ParenthesizedRelationContext context)
    {
        return Visit(context.relation());
    }

    public override SqlNode VisitTableFunctionInvocation(SqlBaseParser.TableFunctionInvocationContext context)
    {
        using var _ = EnterScope();
        string name = SqlIdentifierHelper.NormalizeQualifiedName(context.tableFunctionCall().qualifiedName());
        if (_options.AllowedTableFunctions == null ||
            !SqlFunctionPolicy.ContainsIgnoreCase(_options.AllowedTableFunctions, name))
        {
            throw new SecurityException($"Table function '{name}' is not permitted.");
        }
        throw new NotSupportedException($"Table function '{name}' is allowed by policy but direct AST representation is restricted.");
    }

    public override SqlNode VisitJoinRelation(SqlBaseParser.JoinRelationContext context)
    {
        using var _ = EnterScope();
        var left = (TableSource)Visit(context.left);
        var right = (TableSource)Visit((IParseTree?)context.rightRelation ?? context.right);

        JoinType type;
        if (context.CROSS() != null)
        {
            type = JoinType.Cross;
        }
        else if (context.NATURAL() != null)
        {
            type = JoinType.Natural;
        }
        else if (context.joinType()?.LEFT() != null)
        {
            type = JoinType.LeftOuter;
        }
        else if (context.joinType()?.RIGHT() != null)
        {
            type = JoinType.RightOuter;
        }
        else if (context.joinType()?.FULL() != null)
        {
            type = JoinType.FullOuter;
        }
        else
        {
            type = JoinType.Inner;
        }

        JoinCondition? condition = null;
        if (context.joinCriteria() != null)
        {
            if (context.joinCriteria().ON() != null)
            {
                var pred = (Expression)Visit(context.joinCriteria().booleanExpression());
                condition = new OnJoinCondition(pred);
            }
            else if (context.joinCriteria().USING() != null)
            {
                var cols = context.joinCriteria().identifier().Select(ToSqlIdentifier).ToList();
                condition = new UsingJoinCondition(cols);
            }
        }

        return new JoinedTableSource(left, type, right, condition);
    }

    public override SqlNode VisitDelete(SqlBaseParser.DeleteContext context)
    {
        using var _ = EnterScope();
        var target = new NamedTableSource(ToSqlQualifiedName(context.qualifiedName()), null);
        Expression? where = context.booleanExpression() != null ? (Expression)Visit(context.booleanExpression()) : null;
        return new DeleteStatement(target, where);
    }

    public override SqlNode VisitUpdate(SqlBaseParser.UpdateContext context)
    {
        using var _ = EnterScope();
        var target = new NamedTableSource(ToSqlQualifiedName(context.qualifiedName()), null);
        var assignments = new List<UpdateAssignment>();
        foreach (var ua in context.updateAssignment())
        {
            var col = ToSqlIdentifier(ua.identifier());
            var val = (Expression)Visit(ua.expression());
            assignments.Add(new UpdateAssignment(col, val));
        }
        Expression? where = context.where != null ? (Expression)Visit(context.where) : null;
        return new UpdateStatement(target, assignments, where);
    }

    public override SqlNode VisitInsertInto(SqlBaseParser.InsertIntoContext context)
    {
        using var _ = EnterScope();
        var target = new NamedTableSource(ToSqlQualifiedName(context.qualifiedName()), null);
        IReadOnlyList<SqlIdentifier>? cols = null;
        if (context.columnAliases()?.identifier() != null)
        {
            cols = context.columnAliases().identifier().Select(ToSqlIdentifier).ToList();
        }

        var rootSelect = (SelectStatement)Visit(context.rootQuery());
        return new InsertStatement(target, cols, rootSelect.Body);
    }

    // Expressions
    public override SqlNode VisitExpression(SqlBaseParser.ExpressionContext context) => Visit(context.booleanExpression());

    public override SqlNode VisitLogicalNot(SqlBaseParser.LogicalNotContext context)
    {
        using var _ = EnterScope();
        var operand = (Expression)Visit(context.booleanExpression());
        return new UnaryExpression(UnaryOperator.Not, operand);
    }

    public override SqlNode VisitAnd(SqlBaseParser.AndContext context)
    {
        using var _ = EnterScope();
        var left = (Expression)Visit(context.booleanExpression(0));
        var right = (Expression)Visit(context.booleanExpression(1));
        return new BinaryExpression(left, BinaryOperator.And, right);
    }

    public override SqlNode VisitOr(SqlBaseParser.OrContext context)
    {
        using var _ = EnterScope();
        var left = (Expression)Visit(context.booleanExpression(0));
        var right = (Expression)Visit(context.booleanExpression(1));
        return new BinaryExpression(left, BinaryOperator.Or, right);
    }

    public override SqlNode VisitPredicated(SqlBaseParser.PredicatedContext context)
    {
        var left = (Expression)Visit(context.valueExpression());
        var predicate = context.predicate();
        if (predicate == null) return left;

        switch (predicate)
        {
            case SqlBaseParser.ComparisonContext comp:
                {
                    var right = (Expression)Visit(comp.right);
                    var op = MapComparisonOperator(comp.comparisonOperator());
                    return new BinaryExpression(left, op, right);
                }
            case SqlBaseParser.QuantifiedComparisonContext qc:
                {
                    var subquery = (SelectStatement)Visit(qc.query());
                    var op = MapComparisonOperator(qc.comparisonOperator());
                    var quant = ComparisonQuantifier.Any;
                    if (qc.comparisonQuantifier().ALL() != null) quant = ComparisonQuantifier.All;
                    else if (qc.comparisonQuantifier().SOME() != null) quant = ComparisonQuantifier.Some;
                    return new QuantifiedComparisonExpression(left, op, quant, subquery);
                }
            case SqlBaseParser.BetweenContext bet:
                {
                    var lower = (Expression)Visit(bet.lower);
                    var upper = (Expression)Visit(bet.upper);
                    bool isNot = bet.NOT() != null;
                    return new BetweenExpression(left, lower, upper, isNot);
                }
            case SqlBaseParser.InListContext inList:
                {
                    var items = inList.expression().Select(e => (Expression)Visit(e)).ToList();
                    bool isNot = inList.NOT() != null;
                    return new InListExpression(left, items, isNot);
                }
            case SqlBaseParser.InSubqueryContext inSub:
                {
                    var subquery = (SelectStatement)Visit(inSub.query());
                    bool isNot = inSub.NOT() != null;
                    return new InSubqueryExpression(left, subquery, isNot);
                }
            case SqlBaseParser.LikeContext like:
                {
                    var pattern = (Expression)Visit(like.pattern);
                    Expression? escape = like.escape != null ? (Expression)Visit(like.escape) : null;
                    bool isNot = like.NOT() != null;
                    return new LikeExpression(left, pattern, escape, isNot);
                }
            case SqlBaseParser.NullPredicateContext nullPred:
                {
                    bool isNot = nullPred.NOT() != null;
                    return new UnaryExpression(isNot ? UnaryOperator.IsNotNull : UnaryOperator.IsNull, left);
                }
            case SqlBaseParser.DistinctFromContext dist:
                {
                    var right = (Expression)Visit(dist.right);
                    bool isNot = dist.NOT() != null;
                    return new IsDistinctFromExpression(left, right, isNot);
                }
            case SqlBaseParser.BooleanTestContext bt:
                {
                    bool truth = bt.truthValue.Type == SqlBaseLexer.TRUE;
                    bool isNot = bt.NOT() != null;
                    return new BinaryExpression(
                        left,
                        isNot ? BinaryOperator.NotEqual : BinaryOperator.Equal,
                        new LiteralExpression(truth, LiteralType.Boolean));
                }
            default:
                throw new AstBuildException($"Unsupported predicate type: {predicate.GetType().Name}");
        }
    }

    private static BinaryOperator MapComparisonOperator(SqlBaseParser.ComparisonOperatorContext comp)
    {
        if (comp.EQ() != null) return BinaryOperator.Equal;
        if (comp.NEQ() != null) return BinaryOperator.NotEqual;
        if (comp.LT() != null) return BinaryOperator.LessThan;
        if (comp.LTE() != null) return BinaryOperator.LessThanOrEqual;
        if (comp.GT() != null) return BinaryOperator.GreaterThan;
        if (comp.GTE() != null) return BinaryOperator.GreaterThanOrEqual;
        throw new AstBuildException($"Unknown comparison operator: {comp.GetText()}");
    }

    public override SqlNode VisitValueExpressionDefault(SqlBaseParser.ValueExpressionDefaultContext context)
    {
        return Visit(context.primaryExpression());
    }

    public override SqlNode VisitArithmeticUnary(SqlBaseParser.ArithmeticUnaryContext context)
    {
        using var _ = EnterScope();
        var operand = (Expression)Visit(context.valueExpression());
        var op = context.@operator.Type == SqlBaseLexer.MINUS ? UnaryOperator.Negate : UnaryOperator.Not;
        return new UnaryExpression(op, operand);
    }

    public override SqlNode VisitArithmeticBinary(SqlBaseParser.ArithmeticBinaryContext context)
    {
        using var _ = EnterScope();
        var left = (Expression)Visit(context.left);
        var right = (Expression)Visit(context.right);
        var op = context.@operator.Type switch
        {
            SqlBaseLexer.PLUS => BinaryOperator.Add,
            SqlBaseLexer.MINUS => BinaryOperator.Subtract,
            SqlBaseLexer.ASTERISK => BinaryOperator.Multiply,
            SqlBaseLexer.SLASH => BinaryOperator.Divide,
            SqlBaseLexer.PERCENT => BinaryOperator.Modulo,
            _ => throw new AstBuildException($"Unknown binary operator: {context.@operator.Text}")
        };
        return new BinaryExpression(left, op, right);
    }

    public override SqlNode VisitConcatenation(SqlBaseParser.ConcatenationContext context)
    {
        using var _ = EnterScope();
        var left = (Expression)Visit(context.left);
        var right = (Expression)Visit(context.right);
        return new BinaryExpression(left, BinaryOperator.Concat, right);
    }

    public override SqlNode VisitColumnReference(SqlBaseParser.ColumnReferenceContext context)
    {
        var id = ToSqlIdentifier(context.identifier());
        if (!id.IsQuoted && id.Value.StartsWith("__param_", StringComparison.OrdinalIgnoreCase))
        {
            string paramName = id.Value["__param_".Length..];
            return new ParameterReference(paramName, PositionalIndex: null, IsSynthetic: true);
        }
        return new ColumnReference(new SqlQualifiedName(new[] { id }));
    }

    public override SqlNode VisitDereference(SqlBaseParser.DereferenceContext context)
    {
        using var _ = EnterScope();
        var baseExpr = (Expression)Visit(context.baseExpression);
        var field = ToSqlIdentifier(context.fieldName);

        if (baseExpr is ColumnReference colRef)
        {
            var parts = new List<SqlIdentifier>(colRef.Name.Parts.Count + 1);
            parts.AddRange(colRef.Name.Parts);
            parts.Add(field);
            return new ColumnReference(new SqlQualifiedName(parts));
        }

        // Dereference of non-column expression (e.g. record field access)
        return new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(baseExpr.ToString() ?? string.Empty), field }));
    }

    public override SqlNode VisitParameter(SqlBaseParser.ParameterContext context)
    {
        return new ParameterReference("?");
    }

    public override SqlNode VisitLiterals(SqlBaseParser.LiteralsContext context)
    {
        var lit = context.literal();
        switch (lit)
        {
            case SqlBaseParser.NumericLiteralContext num:
                {
                    string text = num.GetText();
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal))
                    {
                        return new LiteralExpression(intVal, LiteralType.Integer);
                    }
                    if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal decVal))
                    {
                        return new LiteralExpression(decVal, LiteralType.Decimal);
                    }
                    return new LiteralExpression(text, LiteralType.Decimal);
                }
            case SqlBaseParser.StringLiteralContext str:
                {
                    string unquoted = SqlIdentifierHelper.UnquoteStringLiteral(str.GetText());
                    return new LiteralExpression(unquoted, LiteralType.String);
                }
            case SqlBaseParser.BooleanLiteralContext boolLit:
                {
                    bool val = boolLit.booleanValue().TRUE() != null;
                    return new LiteralExpression(val, LiteralType.Boolean);
                }
            case SqlBaseParser.NullLiteralContext:
                return new LiteralExpression(null, LiteralType.Null);
            case SqlBaseParser.TypeConstructorContext typed:
                return BuildTypedLiteral(typed);
            case SqlBaseParser.IntervalLiteralContext interval:
                return BuildIntervalLiteral(interval.interval());
            default:
                // Wunsch 4: typed literals (DATE '…', INTERVAL …), binary and unicode literals are not plain strings.
                throw Unsupported($"literal '{lit.GetType().Name.Replace("Context", string.Empty, StringComparison.Ordinal)}'");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex DateValue = new(@"^\d{4}-\d{2}-\d{2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TimeValue = new(@"^\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TimestampValue = new(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Wunsch 4: DATE/TIME/TIMESTAMP literals (was: turned into a string literal containing the keyword).</summary>
    private static TypedLiteralExpression BuildTypedLiteral(SqlBaseParser.TypeConstructorContext typed)
    {
        if (typed.@string() is not SqlBaseParser.BasicStringLiteralContext str || typed.identifier() == null)
        {
            throw Unsupported("typed literal");
        }

        string type = typed.identifier().GetText().ToUpperInvariant();
        string value = SqlIdentifierHelper.UnquoteStringLiteral(str.GetText());
        var (kind, pattern) = type switch
        {
            "DATE" => (TypedLiteralKind.Date, DateValue),
            "TIME" => (TypedLiteralKind.Time, TimeValue),
            "TIMESTAMP" => (TypedLiteralKind.Timestamp, TimestampValue),
            _ => throw Unsupported($"{type} literal")
        };

        if (!pattern.IsMatch(value))
        {
            throw Unsupported($"{type} literal '{value}' (expected ISO format without time zone)");
        }

        return new TypedLiteralExpression(kind, value);
    }

    /// <summary>Wunsch 4: <c>INTERVAL 'n' FIELD</c> with one unsigned field and an integer value.</summary>
    private static IntervalLiteralExpression BuildIntervalLiteral(SqlBaseParser.IntervalContext interval)
    {
        if (interval.sign != null || interval.@string() is not SqlBaseParser.BasicStringLiteralContext str)
        {
            throw Unsupported("signed or unicode INTERVAL literal");
        }

        string field = interval.intervalQualifier() switch
        {
            SqlBaseParser.SimpleYearMonthIntervalContext ym when ym.precision == null => ym.field.Text,
            SqlBaseParser.SimpleDayTimeIntervalContext dt when dt.precision == null => dt.field.Text,
            SqlBaseParser.SecondsDayTimeIntervalContext s when s.leadingPrecision == null => "SECOND",
            _ => throw Unsupported("INTERVAL with a composite or precision qualifier")
        };

        string value = SqlIdentifierHelper.UnquoteStringLiteral(str.GetText());
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,9}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw Unsupported($"INTERVAL value '{value}' (expected a non-negative integer)");
        }

        return new IntervalLiteralExpression(value, field.ToUpperInvariant());
    }

    public override SqlNode VisitFunctionCall(SqlBaseParser.FunctionCallContext context)
    {
        using var _ = EnterScope();
        string name = SqlIdentifierHelper.NormalizeQualifiedName(context.qualifiedName());
        if (_options.EnforceFunctionPolicy &&
            !SqlFunctionPolicy.IsFunctionAllowed(name, _options.AllowedFunctions, _options.AdditionalDeniedFunctions))
        {
            throw new SecurityException($"Function '{name}' is not permitted by the SQL function policy.");
        }

        // Wunsch 4: modifiers the AST cannot represent yet are rejected instead of being dropped.
        if (context.processingMode() != null) throw Unsupported("RUNNING/FINAL");
        if (context.label != null) throw Unsupported("label.* in function calls");
        if (context.nullTreatment() != null) throw Unsupported("IGNORE/RESPECT NULLS");

        var qName = ToSqlQualifiedName(context.qualifiedName());
        var args = new List<Expression>();
        // Wunsch 4: argument() is an empty array (never null) for COUNT(*), so the star must be checked first.
        bool isStar = context.ASTERISK() != null;
        if (!isStar)
        {
            foreach (var arg in context.argument())
            {
                var exprCtx = arg is SqlBaseParser.PositionalArgumentContext pos
                    ? pos.expression()
                    : arg is SqlBaseParser.NamedArgumentContext named
                        ? named.expression()
                        : null;
                if (exprCtx != null)
                {
                    args.Add((Expression)Visit(exprCtx));
                }
            }
        }

        WindowSpecification? window = null;
        var overCtx = context.over();
        if (overCtx != null && overCtx.windowSpecification() == null)
        {
            throw Unsupported("OVER <window name>");
        }

        if (overCtx?.windowSpecification() != null)
        {
            var winSpec = overCtx.windowSpecification();
            if (winSpec.existingWindowName != null) throw Unsupported("window inheritance");

            var partitionExprs = winSpec._partition != null && winSpec._partition.Count > 0
                ? winSpec._partition.Select(p => (Expression)Visit(p)).ToList().AsReadOnly()
                : null;
            var orderBy = winSpec.orderBy() != null ? BuildOrderBy(winSpec.orderBy()) : null;
            var frame = winSpec.windowFrame() != null ? BuildWindowFrame(winSpec.windowFrame()) : null;
            window = new WindowSpecification(partitionExprs, orderBy, frame);
        }

        var filter = context.filter() != null ? (Expression)Visit(context.filter().booleanExpression()) : null;
        var orderWithin = context.orderBy() != null ? BuildOrderBy(context.orderBy()) : null;

        bool distinct = context.setQuantifier()?.DISTINCT() != null;
        return new FunctionCallExpression(qName, args, distinct, window, isStar, filter, orderWithin);
    }

    public override SqlNode VisitMethodCall(SqlBaseParser.MethodCallContext context)
    {
        throw new SecurityException("Method call syntax (expression.method(...)) is not permitted.");
    }

    public override SqlNode VisitStaticMethodCall(SqlBaseParser.StaticMethodCallContext context)
    {
        throw new SecurityException("Static method call syntax (Type::method(...)) is not permitted.");
    }

    public override SqlNode VisitSubqueryExpression(SqlBaseParser.SubqueryExpressionContext context)
    {
        using var _ = EnterScope();
        var subquery = (SelectStatement)Visit(context.query());
        return new ScalarSubqueryExpression(subquery);
    }

    public override SqlNode VisitExists(SqlBaseParser.ExistsContext context)
    {
        using var _ = EnterScope();
        var subquery = (SelectStatement)Visit(context.query());
        return new ExistsExpression(subquery);
    }

    public override SqlNode VisitSimpleCase(SqlBaseParser.SimpleCaseContext context)
    {
        using var _ = EnterScope();
        var operand = (Expression)Visit(context.operand);
        var whens = new List<WhenClause>();
        foreach (var sw in context.simpleWhenClause())
        {
            var cond = sw.condition != null 
                ? (Expression)Visit(sw.condition) 
                : new LiteralExpression(null, LiteralType.Null);
            var res = (Expression)Visit(sw.result);
            whens.Add(new WhenClause(cond, res));
        }
        Expression? elseRes = context.elseExpression != null ? (Expression)Visit(context.elseExpression) : null;
        return new CaseExpression(operand, whens, elseRes);
    }

    public override SqlNode VisitSearchedCase(SqlBaseParser.SearchedCaseContext context)
    {
        using var _ = EnterScope();
        var whens = new List<WhenClause>();
        foreach (var sw in context.searchedWhenClause())
        {
            var cond = (Expression)Visit(sw.condition);
            var res = (Expression)Visit(sw.result);
            whens.Add(new WhenClause(cond, res));
        }
        Expression? elseRes = context.elseExpression != null ? (Expression)Visit(context.elseExpression) : null;
        return new CaseExpression(null, whens, elseRes);
    }

    public override SqlNode VisitCast(SqlBaseParser.CastContext context)
    {
        using var _ = EnterScope();
        var operand = (Expression)Visit(context.expression());
        // SQL-3: GetText() drops whitespace and keeps quoted identifiers; rebuild from tokens and allow plain type names only.
        var tokens = new List<string>();
        CollectTerminalTexts(context.type(), tokens);
        string targetType = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(string.Join(' ', tokens));
        bool isTryCast = context.TRY_CAST() != null;
        return new CastExpression(operand, targetType, isTryCast);
    }

    private static void CollectTerminalTexts(Antlr4.Runtime.Tree.IParseTree node, List<string> tokens)
    {
        if (node is Antlr4.Runtime.Tree.ITerminalNode terminal)
        {
            tokens.Add(terminal.GetText());
            return;
        }

        for (var i = 0; i < node.ChildCount; i++)
        {
            CollectTerminalTexts(node.GetChild(i), tokens);
        }
    }

    public override SqlNode VisitArrayConstructor(SqlBaseParser.ArrayConstructorContext context)
    {
        using var _ = EnterScope();
        var exprs = context.expression()?.Select(e => (Expression)Visit(e)).ToList() ?? new List<Expression>();
        return new ArrayConstructorExpression(exprs);
    }

    public override SqlNode VisitSubscript(SqlBaseParser.SubscriptContext context)
    {
        using var _ = EnterScope();
        var target = (Expression)Visit(context.value);
        var index = (Expression)Visit(context.index);
        return new SubscriptExpression(target, index);
    }

    public override SqlNode VisitRowConstructor(SqlBaseParser.RowConstructorContext context)
    {
        using var _ = EnterScope();
        var list = new List<Expression>();
        if (context.expression() != null)
        {
            foreach (var e in context.expression())
            {
                list.Add((Expression)Visit(e));
            }
        }
        else if (context.fieldConstructor() != null)
        {
            foreach (var f in context.fieldConstructor())
            {
                list.Add((Expression)Visit(f.expression()));
            }
        }
        return new RowValueExpression(list);
    }

    public override SqlNode VisitParenthesizedExpression(SqlBaseParser.ParenthesizedExpressionContext context)
    {
        using var _ = EnterScope();
        return Visit(context.expression());
    }

    public override SqlNode VisitExtract(SqlBaseParser.ExtractContext context)
    {
        using var _ = EnterScope();
        string field = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureExtractField(context.identifier().GetText());
        var source = (Expression)Visit(context.valueExpression());
        return new ExtractExpression(field, source);
    }

    private static SqlIdentifier ToSqlIdentifier(SqlBaseParser.IdentifierContext context)
    {
        string text = context.GetText();
        bool isQuoted = IsQuotedIdentifier(text);
        string normalized = UnquoteIdentifier(text, isQuoted);
        return new SqlIdentifier(normalized, isQuoted);
    }

    private static SqlQualifiedName ToSqlQualifiedName(SqlBaseParser.QualifiedNameContext context)
    {
        var ids = context.identifier();
        if (ids == null || ids.Length == 0)
        {
            string t = context.GetText();
            bool isQ = IsQuotedIdentifier(t);
            return new SqlQualifiedName(new[] { new SqlIdentifier(UnquoteIdentifier(t, isQ), isQ) });
        }
        var parts = ids.Select(ToSqlIdentifier).ToList();
        return new SqlQualifiedName(parts);
    }

    private static string UnquoteIdentifier(string text, bool isQuoted)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Trim();
        if (isQuoted && text.Length >= 2)
        {
            char first = text[0];
            char last = text[^1];
            if (first == '"' && last == '"') return text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            if (first == '`' && last == '`') return text[1..^1].Replace("``", "`", StringComparison.Ordinal);
            if (first == '[' && last == ']') return text[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }
        return text;
    }

    private static bool IsQuotedIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Length >= 2)
        {
            return (text[0] == '"' && text[^1] == '"') ||
                   (text[0] == '`' && text[^1] == '`') ||
                   (text[0] == '[' && text[^1] == ']');
        }
        return false;
    }
}
