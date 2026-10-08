namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Common base class for SQL dialect generators.
/// Handles operator precedence, recursive expression printing, and default clause generation.
/// </summary>
public abstract class SqlDialectGeneratorBase : ISqlDialectGenerator
{
    public abstract TargetSqlDialect TargetDialect { get; }
    public abstract int MaxParameterBudget { get; }

    public virtual string GenerateSql(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        Span<char> initialBuffer = stackalloc char[512];
        var builder = new ValueStringBuilder(initialBuffer);
        try
        {
            var context = new SqlEmitterContext(TargetDialect, MaxParameterBudget);
            GenerateSql(statement, ref builder, context);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    public virtual void GenerateSql(SqlStatement statement, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);

        switch (statement)
        {
            case SelectStatement select:
                GenerateSelect(select, ref builder, context);
                break;
            case InsertStatement insert:
                GenerateInsert(insert, ref builder, context);
                break;
            case UpdateStatement update:
                GenerateUpdate(update, ref builder, context);
                break;
            case DeleteStatement delete:
                GenerateDelete(delete, ref builder, context);
                break;
            default:
                throw new NotSupportedException($"Unsupported statement type: {statement.GetType().Name}");
        }
    }

    protected virtual void GenerateSelect(SelectStatement statement, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (statement.With != null)
        {
            GenerateWithClause(statement.With, ref builder, context);
            builder.Append(' ');
        }

        GenerateQueryBody(statement.Body, ref builder, context);

        if (statement.OrderBy != null && ShouldEmitOrderByBeforePagination(statement))
        {
            builder.Append(' ');
            GenerateOrderBy(statement.OrderBy, ref builder, context);
        }

        if (statement.Pagination != null)
        {
            builder.Append(' ');
            GeneratePagination(statement.Pagination, statement.OrderBy, ref builder, context);
        }
    }

    protected virtual bool ShouldEmitOrderByBeforePagination(SelectStatement statement) => true;

    protected virtual void GenerateWithClause(WithClause with, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("WITH ");
        if (with.IsRecursive)
        {
            builder.Append("RECURSIVE ");
        }

        for (int i = 0; i < with.Ctes.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var cte = with.Ctes[i];
            FormatIdentifier(ref builder, cte.Name, context);
            if (cte.ColumnAliases != null && cte.ColumnAliases.Count > 0)
            {
                builder.Append(" (");
                for (int j = 0; j < cte.ColumnAliases.Count; j++)
                {
                    if (j > 0) builder.Append(", ");
                    FormatIdentifier(ref builder, cte.ColumnAliases[j], context);
                }
                builder.Append(')');
            }
            builder.Append(" AS (");
            GenerateSelect(cte.Query, ref builder, context);
            builder.Append(')');
        }
    }

    protected virtual void GenerateQueryBody(QueryBody body, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        switch (body)
        {
            case QuerySpecification spec:
                GenerateQuerySpecification(spec, ref builder, context);
                break;
            case SetOperationQuery setOp:
                GenerateSetOperation(setOp, ref builder, context);
                break;
            case ValuesQueryBody values:
                GenerateValuesQueryBody(values, ref builder, context);
                break;
            case TableQueryBody table:
                GenerateTableQueryBody(table, ref builder, context);
                break;
            default:
                throw new NotSupportedException($"Unsupported query body: {body.GetType().Name}");
        }
    }

    protected virtual void GenerateQuerySpecification(QuerySpecification spec, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("SELECT ");
        if (spec.Distinct)
        {
            builder.Append("DISTINCT ");
        }

        bool prevPred = context.InPredicateContext;
        context.InPredicateContext = false;
        context.InProjectionContext = true;
        for (int i = 0; i < spec.Projections.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateSelectItem(spec.Projections[i], ref builder, context);
        }
        context.InProjectionContext = false;
        context.InPredicateContext = prevPred;

        if (spec.From != null)
        {
            builder.Append(" FROM ");
            GenerateTableSource(spec.From, ref builder, context);
        }

        if (spec.Where != null)
        {
            builder.Append(" WHERE ");
            bool prevWherePred = context.InPredicateContext;
            context.InPredicateContext = true;
            GenerateExpression(spec.Where, ref builder, context);
            context.InPredicateContext = prevWherePred;
        }

        if (spec.GroupBy != null && spec.GroupBy.GroupingExpressions.Count > 0)
        {
            builder.Append(" GROUP BY ");
            for (int i = 0; i < spec.GroupBy.GroupingExpressions.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                GenerateExpression(spec.GroupBy.GroupingExpressions[i], ref builder, context);
            }
        }

        if (spec.Having != null)
        {
            builder.Append(" HAVING ");
            bool prevHavingPred = context.InPredicateContext;
            context.InPredicateContext = true;
            GenerateExpression(spec.Having, ref builder, context);
            context.InPredicateContext = prevHavingPred;
        }
    }

    protected virtual void GenerateSelectItem(SelectItem item, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        switch (item)
        {
            case ColumnSelectItem col:
                GenerateExpression(col.Expression, ref builder, context);
                if (col.Alias != null)
                {
                    builder.Append(" AS ");
                    FormatIdentifier(ref builder, col.Alias, context);
                }
                break;
            case WildcardSelectItem wildcard:
                if (wildcard.Qualifier != null)
                {
                    FormatTableName(ref builder, wildcard.Qualifier, context);
                    builder.Append(".*");
                }
                else
                {
                    builder.Append('*');
                }
                break;
        }
    }

    protected virtual string TableAliasKeyword => " AS ";

    protected virtual void GenerateTableSource(TableSource source, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        switch (source)
        {
            case NamedTableSource named:
                FormatTableName(ref builder, named.Name, context);
                if (named.Alias != null)
                {
                    builder.Append(TableAliasKeyword);
                    FormatIdentifier(ref builder, named.Alias, context);
                }
                break;
            case SubqueryTableSource subquery:
                builder.Append('(');
                GenerateSelect(subquery.Subquery, ref builder, context);
                builder.Append(')');
                builder.Append(TableAliasKeyword);
                FormatIdentifier(ref builder, subquery.Alias, context);
                if (subquery.ColumnAliases != null && subquery.ColumnAliases.Count > 0)
                {
                    builder.Append(" (");
                    for (int i = 0; i < subquery.ColumnAliases.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        FormatIdentifier(ref builder, subquery.ColumnAliases[i], context);
                    }
                    builder.Append(')');
                }
                break;
            case JoinedTableSource joined:
                GenerateTableSource(joined.Left, ref builder, context);
                builder.Append(' ');
                builder.Append(GetJoinTypeKeyword(joined.Type));
                builder.Append(' ');
                GenerateTableSource(joined.Right, ref builder, context);
                if (joined.Condition is OnJoinCondition on)
                {
                    builder.Append(" ON ");
                    context.InPredicateContext = true;
                    GenerateExpression(on.Predicate, ref builder, context);
                    context.InPredicateContext = false;
                }
                else if (joined.Condition is UsingJoinCondition usingCond)
                {
                    builder.Append(" USING (");
                    for (int i = 0; i < usingCond.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        FormatIdentifier(ref builder, usingCond.Columns[i], context);
                    }
                    builder.Append(')');
                }
                break;
            case LateralTableSource lateral:
                builder.Append("LATERAL (");
                GenerateSelect(lateral.Subquery, ref builder, context);
                builder.Append(')');
                builder.Append(TableAliasKeyword);
                FormatIdentifier(ref builder, lateral.Alias, context);
                break;
        }
    }

    private static string GetJoinTypeKeyword(JoinType type) => type switch
    {
        JoinType.Inner => "INNER JOIN",
        JoinType.LeftOuter => "LEFT OUTER JOIN",
        JoinType.RightOuter => "RIGHT OUTER JOIN",
        JoinType.FullOuter => "FULL OUTER JOIN",
        JoinType.Cross => "CROSS JOIN",
        JoinType.Natural => "NATURAL JOIN",
        _ => "JOIN"
    };

    protected virtual void GenerateSetOperation(SetOperationQuery setOp, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        GenerateQueryBody(setOp.Left, ref builder, context);
        builder.Append(' ');
        builder.Append(setOp.Operator switch
        {
            SetOperator.Union => "UNION",
            SetOperator.Intersect => "INTERSECT",
            SetOperator.Except => "EXCEPT",
            _ => "UNION"
        });
        if (!setOp.Distinct)
        {
            builder.Append(" ALL");
        }
        builder.Append(' ');
        GenerateQueryBody(setOp.Right, ref builder, context);
    }

    protected virtual void GenerateValuesQueryBody(ValuesQueryBody values, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("VALUES ");
        for (int i = 0; i < values.Rows.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateExpression(values.Rows[i], ref builder, context);
        }
    }

    protected virtual void GenerateTableQueryBody(TableQueryBody table, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("TABLE ");
        FormatTableName(ref builder, table.TableName, context);
    }

    protected virtual void GenerateOrderBy(OrderByClause orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("ORDER BY ");
        for (int i = 0; i < orderBy.Elements.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var el = orderBy.Elements[i];
            GenerateExpression(el.Expression, ref builder, context);
            builder.Append(el.Direction == SortDirection.Descending ? " DESC" : " ASC");
            if (el.NullOrder == NullOrdering.First) builder.Append(" NULLS FIRST");
            else if (el.NullOrder == NullOrdering.Last) builder.Append(" NULLS LAST");
        }
    }

    protected abstract void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context);

    protected virtual void GenerateInsert(InsertStatement insert, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("INSERT INTO ");
        FormatTableName(ref builder, insert.TargetTable.Name, context);
        if (insert.Columns != null && insert.Columns.Count > 0)
        {
            builder.Append(" (");
            for (int i = 0; i < insert.Columns.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                FormatIdentifier(ref builder, insert.Columns[i], context);
            }
            builder.Append(')');
        }
        builder.Append(' ');
        GenerateQueryBody(insert.Source, ref builder, context);
    }

    protected virtual void GenerateUpdate(UpdateStatement update, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("UPDATE ");
        FormatTableName(ref builder, update.TargetTable.Name, context);
        builder.Append(" SET ");
        for (int i = 0; i < update.Assignments.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var a = update.Assignments[i];
            FormatIdentifier(ref builder, a.Column, context);
            builder.Append(" = ");
            GenerateExpression(a.Value, ref builder, context);
        }

        if (update.Where != null)
        {
            builder.Append(" WHERE ");
            context.InPredicateContext = true;
            GenerateExpression(update.Where, ref builder, context);
            context.InPredicateContext = false;
        }
    }

    protected virtual void GenerateDelete(DeleteStatement delete, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("DELETE FROM ");
        FormatTableName(ref builder, delete.TargetTable.Name, context);
        if (delete.Where != null)
        {
            builder.Append(" WHERE ");
            context.InPredicateContext = true;
            GenerateExpression(delete.Where, ref builder, context);
            context.InPredicateContext = false;
        }
    }

    // Expression emitter
    public virtual void GenerateExpression(Expression expression, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        switch (expression)
        {
            case ColumnReference col:
                FormatQualifiedName(ref builder, col.Name, context);
                break;
            case ParameterReference param:
                FormatParameter(ref builder, param, context);
                break;
            case LiteralExpression lit:
                FormatLiteral(ref builder, lit, context);
                break;
            case BinaryExpression b:
                FormatBinaryExpression(ref builder, b, context);
                break;
            case UnaryExpression u:
                FormatUnaryExpression(ref builder, u, context);
                break;
            case LikeExpression lk:
                GenerateExpression(lk.Operand, ref builder, context);
                builder.Append(lk.IsNotLike ? " NOT LIKE " : " LIKE ");
                GenerateExpression(lk.Pattern, ref builder, context);
                if (lk.Escape != null)
                {
                    builder.Append(" ESCAPE ");
                    GenerateExpression(lk.Escape, ref builder, context);
                }
                break;
            case InListExpression inL:
                GenerateExpression(inL.Operand, ref builder, context);
                builder.Append(inL.IsNotIn ? " NOT IN (" : " IN (");
                for (int i = 0; i < inL.Items.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    GenerateExpression(inL.Items[i], ref builder, context);
                }
                builder.Append(')');
                break;
            case InSubqueryExpression inSq:
                GenerateExpression(inSq.Operand, ref builder, context);
                builder.Append(inSq.IsNotIn ? " NOT IN (" : " IN (");
                GenerateSelect(inSq.Subquery, ref builder, context);
                builder.Append(')');
                break;
            case ExistsExpression ex:
                builder.Append("EXISTS (");
                GenerateSelect(ex.Subquery, ref builder, context);
                builder.Append(')');
                break;
            case ScalarSubqueryExpression sc:
                builder.Append('(');
                GenerateSelect(sc.Subquery, ref builder, context);
                builder.Append(')');
                break;
            case QuantifiedComparisonExpression qc:
                GenerateExpression(qc.Left, ref builder, context);
                builder.Append(' ');
                builder.Append(GetBinaryOperatorString(qc.Operator));
                builder.Append(' ');
                builder.Append(qc.Quantifier.ToString().ToUpperInvariant());
                builder.Append(" (");
                GenerateSelect(qc.Subquery, ref builder, context);
                builder.Append(')');
                break;
            case BetweenExpression bt:
                GenerateExpression(bt.Operand, ref builder, context);
                builder.Append(bt.IsNotBetween ? " NOT BETWEEN " : " BETWEEN ");
                GenerateExpression(bt.Lower, ref builder, context);
                builder.Append(" AND ");
                GenerateExpression(bt.Upper, ref builder, context);
                break;
            case IsDistinctFromExpression dist:
                FormatIsDistinctFrom(ref builder, dist, context);
                break;
            case CaseExpression cs:
                builder.Append("CASE");
                if (cs.Operand != null)
                {
                    builder.Append(' ');
                    GenerateExpression(cs.Operand, ref builder, context);
                }
                foreach (var w in cs.WhenClauses)
                {
                    builder.Append(" WHEN ");
                    bool prevCasePred = context.InPredicateContext;
                    context.InPredicateContext = true;
                    GenerateExpression(w.Condition, ref builder, context);
                    context.InPredicateContext = prevCasePred;
                    builder.Append(" THEN ");
                    GenerateExpression(w.Result, ref builder, context);
                }
                if (cs.ElseResult != null)
                {
                    builder.Append(" ELSE ");
                    GenerateExpression(cs.ElseResult, ref builder, context);
                }
                builder.Append(" END");
                break;
            case FunctionCallExpression fn:
                FormatQualifiedName(ref builder, fn.Name, context);
                builder.Append('(');
                if (fn.Distinct) builder.Append("DISTINCT ");
                for (int i = 0; i < fn.Arguments.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    GenerateExpression(fn.Arguments[i], ref builder, context);
                }
                builder.Append(')');
                if (fn.Window != null)
                {
                    builder.Append(" OVER (");
                    if (fn.Window.PartitionBy != null && fn.Window.PartitionBy.Count > 0)
                    {
                        builder.Append("PARTITION BY ");
                        for (int i = 0; i < fn.Window.PartitionBy.Count; i++)
                        {
                            if (i > 0) builder.Append(", ");
                            GenerateExpression(fn.Window.PartitionBy[i], ref builder, context);
                        }
                    }
                    if (fn.Window.OrderBy != null)
                    {
                        if (fn.Window.PartitionBy != null && fn.Window.PartitionBy.Count > 0) builder.Append(' ');
                        GenerateOrderBy(fn.Window.OrderBy, ref builder, context);
                    }
                    builder.Append(')');
                }
                break;
            case CastExpression cast:
                builder.Append(cast.IsTryCast ? "TRY_CAST(" : "CAST(");
                GenerateExpression(cast.Operand, ref builder, context);
                builder.Append(" AS ");
                builder.Append(TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(cast.TargetType));
                builder.Append(')');
                break;
            case RowValueExpression row:
                builder.Append('(');
                for (int i = 0; i < row.Elements.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    GenerateExpression(row.Elements[i], ref builder, context);
                }
                builder.Append(')');
                break;
            case ArrayConstructorExpression arr:
                FormatArrayConstructor(ref builder, arr, context);
                break;
            case SubscriptExpression sub:
                GenerateExpression(sub.Target, ref builder, context);
                builder.Append('[');
                GenerateExpression(sub.Index, ref builder, context);
                builder.Append(']');
                break;
            case TrustedSqlExpression trusted:
                // Only used as a projection item (column masks), so no precedence parentheses are needed.
                builder.Append(trusted.Sql);
                break;
            case ExtractExpression ext:
                builder.Append("EXTRACT(");
                builder.Append(TrinoSqlEngine.Ast.SqlSafeTokens.EnsureExtractField(ext.Field));
                builder.Append(" FROM ");
                GenerateExpression(ext.Source, ref builder, context);
                builder.Append(')');
                break;
            default:
                throw new NotSupportedException($"Unsupported expression: {expression.GetType().Name}");
        }
    }

    protected virtual void FormatBinaryExpression(ref ValueStringBuilder builder, BinaryExpression b, SqlEmitterContext context)
    {
        bool parensLeft = NeedsParentheses(b.Left, b.Operator, isLeft: true);
        bool parensRight = NeedsParentheses(b.Right, b.Operator, isLeft: false);

        if (parensLeft) builder.Append('(');
        GenerateExpression(b.Left, ref builder, context);
        if (parensLeft) builder.Append(')');

        builder.Append(' ');
        builder.Append(GetBinaryOperatorString(b.Operator));
        builder.Append(' ');

        if (parensRight) builder.Append('(');
        GenerateExpression(b.Right, ref builder, context);
        if (parensRight) builder.Append(')');
    }

    private static readonly Regex SafeParamIdentifierRegex = new(
        @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    protected virtual void FormatParameter(ref ValueStringBuilder builder, ParameterReference param, SqlEmitterContext context)
    {
        if (!string.IsNullOrWhiteSpace(param.Name) && param.Name != "?" && !param.Name.StartsWith('$') && !param.IsSynthetic)
        {
            var rawName = param.Name.TrimStart('@', ':');
            if (SafeParamIdentifierRegex.IsMatch(rawName))
            {
                switch (context.Dialect)
                {
                    case TargetSqlDialect.SqlServer:
                    case TargetSqlDialect.Sqlite:
                        builder.Append('@');
                        builder.Append(rawName);
                        return;
                    case TargetSqlDialect.Oracle:
                    case TargetSqlDialect.Snowflake:
                        builder.Append(':');
                        builder.Append(rawName);
                        return;
                }
            }
        }

        context.FormatNextParameterMarker(ref builder);
    }

    protected virtual void FormatUnaryExpression(ref ValueStringBuilder builder, UnaryExpression u, SqlEmitterContext context)
    {
        switch (u.Operator)
        {
            case UnaryOperator.Not:
                builder.Append("NOT (");
                GenerateExpression(u.Operand, ref builder, context);
                builder.Append(')');
                break;
            case UnaryOperator.Negate:
                builder.Append("-(");
                GenerateExpression(u.Operand, ref builder, context);
                builder.Append(')');
                break;
            case UnaryOperator.IsNull:
                if (u.Operand is BinaryExpression or BetweenExpression or LikeExpression)
                {
                    builder.Append('(');
                    GenerateExpression(u.Operand, ref builder, context);
                    builder.Append(") IS NULL");
                }
                else
                {
                    GenerateExpression(u.Operand, ref builder, context);
                    builder.Append(" IS NULL");
                }
                break;
            case UnaryOperator.IsNotNull:
                if (u.Operand is BinaryExpression or BetweenExpression or LikeExpression)
                {
                    builder.Append('(');
                    GenerateExpression(u.Operand, ref builder, context);
                    builder.Append(") IS NOT NULL");
                }
                else
                {
                    GenerateExpression(u.Operand, ref builder, context);
                    builder.Append(" IS NOT NULL");
                }
                break;
        }
    }

    protected virtual void FormatIsDistinctFrom(ref ValueStringBuilder builder, IsDistinctFromExpression dist, SqlEmitterContext context)
    {
        GenerateExpression(dist.Left, ref builder, context);
        builder.Append(dist.IsNotDistinctFrom ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ");
        GenerateExpression(dist.Right, ref builder, context);
    }

    protected virtual void FormatArrayConstructor(ref ValueStringBuilder builder, ArrayConstructorExpression arr, SqlEmitterContext context)
    {
        builder.Append("ARRAY[");
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateExpression(arr.Elements[i], ref builder, context);
        }
        builder.Append(']');
    }

    protected virtual string GetBinaryOperatorString(BinaryOperator op) => op switch
    {
        BinaryOperator.And => "AND",
        BinaryOperator.Or => "OR",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        BinaryOperator.Concat => "||",
        _ => throw new NotSupportedException($"Unknown operator: {op}")
    };

    private static bool NeedsParentheses(Expression child, BinaryOperator parentOp, bool isLeft)
    {
        if (child is BinaryExpression childBinary)
        {
            int parentPrec = GetPrecedence(parentOp);
            int childPrec = GetPrecedence(childBinary.Operator);
            if (childPrec < parentPrec) return true;
            if (childPrec == parentPrec && !isLeft && (parentOp == BinaryOperator.Subtract || parentOp == BinaryOperator.Divide || parentOp == BinaryOperator.Modulo))
                return true;
            return false;
        }

        if (child is BetweenExpression && parentOp is BinaryOperator.And or BinaryOperator.Or)
        {
            return true;
        }

        return false;
    }

    private static int GetPrecedence(BinaryOperator op) => op switch
    {
        BinaryOperator.Or => 1,
        BinaryOperator.And => 2,
        BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => 3,
        BinaryOperator.Concat => 4,
        BinaryOperator.Add or BinaryOperator.Subtract => 5,
        BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => 6,
        _ => 0
    };

    public abstract void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context);

    public virtual void FormatQualifiedName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        int startIndex = name.Parts.Count == 4 ? 1 : 0;
        for (int i = startIndex; i < name.Parts.Count; i++)
        {
            if (i > startIndex) builder.Append('.');
            FormatIdentifier(ref builder, name.Parts[i], context);
        }
    }

    /// <summary>
    /// Emits a table reference for the backend target dialect. If the table reference is a 3-part name
    /// (catalog.schema.table), the catalog prefix is stripped to emit only schema.table.
    /// </summary>
    public virtual void FormatTableName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        int startIndex = name.Parts.Count == 3 ? 1 : 0;
        for (int i = startIndex; i < name.Parts.Count; i++)
        {
            if (i > startIndex) builder.Append('.');
            FormatIdentifier(ref builder, name.Parts[i], context);
        }
    }

    public abstract void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context);

    public abstract void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context);

    public virtual void FormatLiteral(ref ValueStringBuilder builder, LiteralExpression lit, SqlEmitterContext context)
    {
        if (lit.Value == null || lit.Type == LiteralType.Null)
        {
            builder.Append("NULL");
            return;
        }

        switch (lit.Type)
        {
            case LiteralType.Boolean:
                FormatBoolean(ref builder, (bool)lit.Value, context);
                break;
            case LiteralType.Integer:
                builder.Append(Convert.ToInt64(lit.Value, CultureInfo.InvariantCulture));
                break;
            case LiteralType.Decimal:
                builder.Append(Convert.ToString(lit.Value, CultureInfo.InvariantCulture));
                break;
            case LiteralType.String:
                FormatStringLiteral(ref builder, lit.Value.ToString() ?? string.Empty, context);
                break;
            case LiteralType.Binary:
                builder.Append(TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(lit.Value.ToString() ?? string.Empty));
                break;
            default:
                builder.Append(lit.Value.ToString() ?? string.Empty);
                break;
        }
    }
}
