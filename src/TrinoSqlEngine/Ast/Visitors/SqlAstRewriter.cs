namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Base AST rewriter providing functional immutable node transformations.
/// Returns the identical node instance if no children were modified.
/// </summary>
public class SqlAstRewriter : ISqlAstVisitor<SqlNode>
{
    public virtual SqlNode Visit(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            SelectStatement s => VisitSelectStatement(s),
            InsertStatement i => VisitInsertStatement(i),
            UpdateStatement u => VisitUpdateStatement(u),
            DeleteStatement d => VisitDeleteStatement(d),
            QuerySpecification qs => VisitQuerySpecification(qs),
            SetOperationQuery so => VisitSetOperationQuery(so),
            ValuesQueryBody v => VisitValuesQueryBody(v),
            TableQueryBody t => VisitTableQueryBody(t),
            ColumnSelectItem c => VisitColumnSelectItem(c),
            WildcardSelectItem w => VisitWildcardSelectItem(w),
            WithClause with => VisitWithClause(with),
            CommonTableExpression cte => VisitCommonTableExpression(cte),
            NamedTableSource n => VisitNamedTableSource(n),
            SubqueryTableSource sq => VisitSubqueryTableSource(sq),
            JoinedTableSource j => VisitJoinedTableSource(j),
            LateralTableSource l => VisitLateralTableSource(l),
            OnJoinCondition on => VisitOnJoinCondition(on),
            UsingJoinCondition using_ => VisitUsingJoinCondition(using_),
            ColumnReference cr => VisitColumnReference(cr),
            ParameterReference p => VisitParameterReference(p),
            LiteralExpression lit => VisitLiteralExpression(lit),
            BinaryExpression b => VisitBinaryExpression(b),
            UnaryExpression un => VisitUnaryExpression(un),
            LikeExpression lk => VisitLikeExpression(lk),
            InListExpression inL => VisitInListExpression(inL),
            InSubqueryExpression inSq => VisitInSubqueryExpression(inSq),
            ExistsExpression ex => VisitExistsExpression(ex),
            ScalarSubqueryExpression sc => VisitScalarSubqueryExpression(sc),
            QuantifiedComparisonExpression qc => VisitQuantifiedComparisonExpression(qc),
            IsDistinctFromExpression df => VisitIsDistinctFromExpression(df),
            BetweenExpression bt => VisitBetweenExpression(bt),
            CaseExpression cs => VisitCaseExpression(cs),
            WhenClause wh => VisitWhenClause(wh),
            FunctionCallExpression fn => VisitFunctionCallExpression(fn),
            WindowSpecification win => VisitWindowSpecification(win),
            CastExpression cast => VisitCastExpression(cast),
            RowValueExpression row => VisitRowValueExpression(row),
            ArrayConstructorExpression arr => VisitArrayConstructorExpression(arr),
            SubscriptExpression sub => VisitSubscriptExpression(sub),
            ExtractExpression ext => VisitExtractExpression(ext),
            OrderByClause ord => VisitOrderByClause(ord),
            OrderByElement el => VisitOrderByElement(el),
            PaginationClause pag => VisitPaginationClause(pag),
            GroupByClause grp => VisitGroupByClause(grp),
            UpdateAssignment ua => VisitUpdateAssignment(ua),
            SqlIdentifier id => VisitSqlIdentifier(id),
            SqlQualifiedName qn => VisitSqlQualifiedName(qn),
            _ => throw new NotSupportedException($"Unsupported AST node type: {node.GetType().Name}")
        };
    }

    public virtual SqlNode VisitSelectStatement(SelectStatement node)
    {
        var with = node.With != null ? (WithClause)Visit(node.With) : null;
        var body = (QueryBody)Visit(node.Body);
        var orderBy = node.OrderBy != null ? (OrderByClause)Visit(node.OrderBy) : null;
        var pagination = node.Pagination != null ? (PaginationClause)Visit(node.Pagination) : null;

        if (with == node.With && body == node.Body && orderBy == node.OrderBy && pagination == node.Pagination)
            return node;

        return node with { With = with, Body = body, OrderBy = orderBy, Pagination = pagination };
    }

    public virtual SqlNode VisitInsertStatement(InsertStatement node)
    {
        var target = (NamedTableSource)Visit(node.TargetTable);
        var cols = node.Columns != null ? RewriteList(node.Columns, id => (SqlIdentifier)Visit(id)) : null;
        var source = (QueryBody)Visit(node.Source);

        if (target == node.TargetTable && cols == node.Columns && source == node.Source)
            return node;

        return node with { TargetTable = target, Columns = cols, Source = source };
    }

    public virtual SqlNode VisitUpdateStatement(UpdateStatement node)
    {
        var target = (NamedTableSource)Visit(node.TargetTable);
        var assignments = RewriteList(node.Assignments, a => (UpdateAssignment)Visit(a));
        var where = node.Where != null ? (Expression)Visit(node.Where) : null;

        if (target == node.TargetTable && assignments == node.Assignments && where == node.Where)
            return node;

        return node with { TargetTable = target, Assignments = assignments, Where = where };
    }

    public virtual SqlNode VisitDeleteStatement(DeleteStatement node)
    {
        var target = (NamedTableSource)Visit(node.TargetTable);
        var where = node.Where != null ? (Expression)Visit(node.Where) : null;

        if (target == node.TargetTable && where == node.Where)
            return node;

        return node with { TargetTable = target, Where = where };
    }

    public virtual SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        var projections = RewriteList(node.Projections, p => (SelectItem)Visit(p));
        var from = node.From != null ? (TableSource)Visit(node.From) : null;
        var where = node.Where != null ? (Expression)Visit(node.Where) : null;
        var groupBy = node.GroupBy != null ? (GroupByClause)Visit(node.GroupBy) : null;
        var having = node.Having != null ? (Expression)Visit(node.Having) : null;

        if (projections == node.Projections && from == node.From && where == node.Where && groupBy == node.GroupBy && having == node.Having)
            return node;

        return node with { Projections = projections, From = from, Where = where, GroupBy = groupBy, Having = having };
    }

    public virtual SqlNode VisitSetOperationQuery(SetOperationQuery node)
    {
        var left = (QueryBody)Visit(node.Left);
        var right = (QueryBody)Visit(node.Right);

        if (left == node.Left && right == node.Right)
            return node;

        return node with { Left = left, Right = right };
    }

    public virtual SqlNode VisitValuesQueryBody(ValuesQueryBody node)
    {
        var rows = RewriteList(node.Rows, r => (RowValueExpression)Visit(r));
        if (rows == node.Rows) return node;
        return node with { Rows = rows };
    }

    public virtual SqlNode VisitTableQueryBody(TableQueryBody node)
    {
        var name = (SqlQualifiedName)Visit(node.TableName);
        if (name == node.TableName) return node;
        return node with { TableName = name };
    }

    public virtual SqlNode VisitColumnSelectItem(ColumnSelectItem node)
    {
        var expr = (Expression)Visit(node.Expression);
        var alias = node.Alias != null ? (SqlIdentifier)Visit(node.Alias) : null;

        if (expr == node.Expression && alias == node.Alias)
            return node;

        return node with { Expression = expr, Alias = alias };
    }

    public virtual SqlNode VisitWildcardSelectItem(WildcardSelectItem node)
    {
        var qual = node.Qualifier != null ? (SqlQualifiedName)Visit(node.Qualifier) : null;
        if (qual == node.Qualifier) return node;
        return node with { Qualifier = qual };
    }

    public virtual SqlNode VisitWithClause(WithClause node)
    {
        var ctes = RewriteList(node.Ctes, c => (CommonTableExpression)Visit(c));
        if (ctes == node.Ctes) return node;
        return node with { Ctes = ctes };
    }

    public virtual SqlNode VisitCommonTableExpression(CommonTableExpression node)
    {
        var name = (SqlIdentifier)Visit(node.Name);
        var aliases = node.ColumnAliases != null ? RewriteList(node.ColumnAliases, id => (SqlIdentifier)Visit(id)) : null;
        var query = (SelectStatement)Visit(node.Query);

        if (name == node.Name && aliases == node.ColumnAliases && query == node.Query)
            return node;

        return node with { Name = name, ColumnAliases = aliases, Query = query };
    }

    public virtual SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        var name = (SqlQualifiedName)Visit(node.Name);
        var alias = node.Alias != null ? (SqlIdentifier)Visit(node.Alias) : null;

        if (name == node.Name && alias == node.Alias)
            return node;

        return node with { Name = name, Alias = alias };
    }

    public virtual SqlNode VisitSubqueryTableSource(SubqueryTableSource node)
    {
        var subquery = (SelectStatement)Visit(node.Subquery);
        var alias = (SqlIdentifier)Visit(node.Alias);
        var colAliases = node.ColumnAliases != null ? RewriteList(node.ColumnAliases, id => (SqlIdentifier)Visit(id)) : null;

        if (subquery == node.Subquery && alias == node.Alias && colAliases == node.ColumnAliases)
            return node;

        return node with { Subquery = subquery, Alias = alias, ColumnAliases = colAliases };
    }

    public virtual SqlNode VisitJoinedTableSource(JoinedTableSource node)
    {
        var left = (TableSource)Visit(node.Left);
        var right = (TableSource)Visit(node.Right);
        var cond = node.Condition != null ? (JoinCondition)Visit(node.Condition) : null;

        if (left == node.Left && right == node.Right && cond == node.Condition)
            return node;

        return node with { Left = left, Right = right, Condition = cond };
    }

    public virtual SqlNode VisitLateralTableSource(LateralTableSource node)
    {
        var subquery = (SelectStatement)Visit(node.Subquery);
        var alias = (SqlIdentifier)Visit(node.Alias);
        var colAliases = node.ColumnAliases != null ? RewriteList(node.ColumnAliases, id => (SqlIdentifier)Visit(id)) : null;

        if (subquery == node.Subquery && alias == node.Alias && colAliases == node.ColumnAliases)
            return node;

        return node with { Subquery = subquery, Alias = alias, ColumnAliases = colAliases };
    }

    public virtual SqlNode VisitOnJoinCondition(OnJoinCondition node)
    {
        var pred = (Expression)Visit(node.Predicate);
        if (pred == node.Predicate) return node;
        return node with { Predicate = pred };
    }

    public virtual SqlNode VisitUsingJoinCondition(UsingJoinCondition node)
    {
        var cols = RewriteList(node.Columns, id => (SqlIdentifier)Visit(id));
        if (cols == node.Columns) return node;
        return node with { Columns = cols };
    }

    public virtual SqlNode VisitColumnReference(ColumnReference node)
    {
        var name = (SqlQualifiedName)Visit(node.Name);
        if (name == node.Name) return node;
        return node with { Name = name };
    }

    public virtual SqlNode VisitParameterReference(ParameterReference node) => node;

    public virtual SqlNode VisitLiteralExpression(LiteralExpression node) => node;

    public virtual SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        var left = (Expression)Visit(node.Left);
        var right = (Expression)Visit(node.Right);

        if (left == node.Left && right == node.Right)
            return node;

        return node with { Left = left, Right = right };
    }

    public virtual SqlNode VisitUnaryExpression(UnaryExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        if (operand == node.Operand) return node;
        return node with { Operand = operand };
    }

    public virtual SqlNode VisitLikeExpression(LikeExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        var pattern = (Expression)Visit(node.Pattern);
        var escape = node.Escape != null ? (Expression)Visit(node.Escape) : null;

        if (operand == node.Operand && pattern == node.Pattern && escape == node.Escape)
            return node;

        return node with { Operand = operand, Pattern = pattern, Escape = escape };
    }

    public virtual SqlNode VisitInListExpression(InListExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        var items = RewriteList(node.Items, i => (Expression)Visit(i));

        if (operand == node.Operand && items == node.Items)
            return node;

        return node with { Operand = operand, Items = items };
    }

    public virtual SqlNode VisitInSubqueryExpression(InSubqueryExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        var subquery = (SelectStatement)Visit(node.Subquery);

        if (operand == node.Operand && subquery == node.Subquery)
            return node;

        return node with { Operand = operand, Subquery = subquery };
    }

    public virtual SqlNode VisitExistsExpression(ExistsExpression node)
    {
        var subquery = (SelectStatement)Visit(node.Subquery);
        if (subquery == node.Subquery) return node;
        return node with { Subquery = subquery };
    }

    public virtual SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node)
    {
        var subquery = (SelectStatement)Visit(node.Subquery);
        if (subquery == node.Subquery) return node;
        return node with { Subquery = subquery };
    }

    public virtual SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node)
    {
        var left = (Expression)Visit(node.Left);
        var subquery = (SelectStatement)Visit(node.Subquery);

        if (left == node.Left && subquery == node.Subquery)
            return node;

        return node with { Left = left, Subquery = subquery };
    }

    public virtual SqlNode VisitIsDistinctFromExpression(IsDistinctFromExpression node)
    {
        var left = (Expression)Visit(node.Left);
        var right = (Expression)Visit(node.Right);

        if (left == node.Left && right == node.Right)
            return node;

        return node with { Left = left, Right = right };
    }

    public virtual SqlNode VisitBetweenExpression(BetweenExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        var lower = (Expression)Visit(node.Lower);
        var upper = (Expression)Visit(node.Upper);

        if (operand == node.Operand && lower == node.Lower && upper == node.Upper)
            return node;

        return node with { Operand = operand, Lower = lower, Upper = upper };
    }

    public virtual SqlNode VisitCaseExpression(CaseExpression node)
    {
        var operand = node.Operand != null ? (Expression)Visit(node.Operand) : null;
        var whens = RewriteList(node.WhenClauses, w => (WhenClause)Visit(w));
        var elseResult = node.ElseResult != null ? (Expression)Visit(node.ElseResult) : null;

        if (operand == node.Operand && whens == node.WhenClauses && elseResult == node.ElseResult)
            return node;

        return node with { Operand = operand, WhenClauses = whens, ElseResult = elseResult };
    }

    public virtual SqlNode VisitWhenClause(WhenClause node)
    {
        var cond = (Expression)Visit(node.Condition);
        var res = (Expression)Visit(node.Result);

        if (cond == node.Condition && res == node.Result)
            return node;

        return node with { Condition = cond, Result = res };
    }

    public virtual SqlNode VisitFunctionCallExpression(FunctionCallExpression node)
    {
        var name = (SqlQualifiedName)Visit(node.Name);
        var args = RewriteList(node.Arguments, a => (Expression)Visit(a));
        var win = node.Window != null ? (WindowSpecification)Visit(node.Window) : null;

        if (name == node.Name && args == node.Arguments && win == node.Window)
            return node;

        return node with { Name = name, Arguments = args, Window = win };
    }

    public virtual SqlNode VisitWindowSpecification(WindowSpecification node)
    {
        var partBy = node.PartitionBy != null ? RewriteList(node.PartitionBy, p => (Expression)Visit(p)) : null;
        var ord = node.OrderBy != null ? (OrderByClause)Visit(node.OrderBy) : null;

        if (partBy == node.PartitionBy && ord == node.OrderBy)
            return node;

        return node with { PartitionBy = partBy, OrderBy = ord };
    }

    public virtual SqlNode VisitCastExpression(CastExpression node)
    {
        var operand = (Expression)Visit(node.Operand);
        if (operand == node.Operand) return node;
        return node with { Operand = operand };
    }

    public virtual SqlNode VisitRowValueExpression(RowValueExpression node)
    {
        var elements = RewriteList(node.Elements, e => (Expression)Visit(e));
        if (elements == node.Elements) return node;
        return node with { Elements = elements };
    }

    public virtual SqlNode VisitArrayConstructorExpression(ArrayConstructorExpression node)
    {
        var elements = RewriteList(node.Elements, e => (Expression)Visit(e));
        if (elements == node.Elements) return node;
        return node with { Elements = elements };
    }

    public virtual SqlNode VisitSubscriptExpression(SubscriptExpression node)
    {
        var target = (Expression)Visit(node.Target);
        var index = (Expression)Visit(node.Index);

        if (target == node.Target && index == node.Index)
            return node;

        return node with { Target = target, Index = index };
    }

    public virtual SqlNode VisitExtractExpression(ExtractExpression node)
    {
        var source = (Expression)Visit(node.Source);
        if (source == node.Source) return node;
        return node with { Source = source };
    }

    public virtual SqlNode VisitOrderByClause(OrderByClause node)
    {
        var elements = RewriteList(node.Elements, e => (OrderByElement)Visit(e));
        if (elements == node.Elements) return node;
        return node with { Elements = elements };
    }

    public virtual SqlNode VisitOrderByElement(OrderByElement node)
    {
        var expr = (Expression)Visit(node.Expression);
        if (expr == node.Expression) return node;
        return node with { Expression = expr };
    }

    public virtual SqlNode VisitPaginationClause(PaginationClause node)
    {
        var offset = node.Offset != null ? (Expression)Visit(node.Offset) : null;
        var limit = node.Limit != null ? (Expression)Visit(node.Limit) : null;

        if (offset == node.Offset && limit == node.Limit)
            return node;

        return node with { Offset = offset, Limit = limit };
    }

    public virtual SqlNode VisitGroupByClause(GroupByClause node)
    {
        var exprs = RewriteList(node.GroupingExpressions, e => (Expression)Visit(e));
        if (exprs == node.GroupingExpressions) return node;
        return node with { GroupingExpressions = exprs };
    }

    public virtual SqlNode VisitUpdateAssignment(UpdateAssignment node)
    {
        var col = (SqlIdentifier)Visit(node.Column);
        var val = (Expression)Visit(node.Value);

        if (col == node.Column && val == node.Value)
            return node;

        return node with { Column = col, Value = val };
    }

    public virtual SqlNode VisitSqlIdentifier(SqlIdentifier node) => node;

    public virtual SqlNode VisitSqlQualifiedName(SqlQualifiedName node)
    {
        var parts = RewriteList(node.Parts, id => (SqlIdentifier)Visit(id));
        if (parts == node.Parts) return node;
        return new SqlQualifiedName(parts);
    }

    protected static IReadOnlyList<T> RewriteList<T>(IReadOnlyList<T> list, Func<T, T> visitItem)
    {
        List<T>? newList = null;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var transformed = visitItem(item);
            if (!EqualityComparer<T>.Default.Equals(item, transformed) && newList == null)
            {
                newList = new List<T>(list.Count);
                for (int j = 0; j < i; j++)
                {
                    newList.Add(list[j]);
                }
            }
            newList?.Add(transformed);
        }
        return newList != null ? newList.AsReadOnly() : list;
    }
}
