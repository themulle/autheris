namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
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

    /// <summary>Capabilities from the capability table; a dialect without an entry throws (fail closed).</summary>
    public virtual DialectCapabilities Capabilities => DialectCapabilityTable.Default.Get(TargetDialect);

    /// <summary>
    /// Generates parameterized SQL: every client, tenant, policy and mask value is collected as a
    /// <see cref="BoundParameter"/> and counted against the capability bind limit. The emitted text is verified by
    /// <see cref="EmittedSqlInvariantChecker"/> before it is returned.
    /// </summary>
    public virtual CompiledSql Generate(SqlStatement statement, ParameterSource values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(values);

        var capabilities = Capabilities;
        cancellationToken.ThrowIfCancellationRequested();
        var context = new SqlEmitterContext(TargetDialect, capabilities, values, cancellationToken);
        Span<char> initialBuffer = stackalloc char[512];
        var builder = new ValueStringBuilder(initialBuffer);
        string sql;
        try
        {
            GenerateSql(statement, ref builder, context);
            sql = builder.ToString();
        }
        catch (InsufficientExecutionStackException)
        {
            // SEC-ADG-05: a StackOverflowException cannot be caught, so deep trees are rejected before the stack runs out.
            throw new SqlLimitExceededException(SqlLimitKind.NestingDepth, TargetDialect, 0, 0);
        }
        finally
        {
            builder.Dispose();
        }

        var parameters = context.Parameters;
        EmittedSqlInvariantChecker.Check(sql, TargetDialect, parameters, context.Ranges, structuralOnly: !BindLiterals);

        return new CompiledSql(
            sql,
            parameters,
            TargetDialect,
            statement switch
            {
                SelectStatement => SqlStatementClass.Select,
                InsertStatement => SqlStatementClass.Insert,
                UpdateStatement => SqlStatementClass.Update,
                DeleteStatement => SqlStatementClass.Delete,
                _ => throw new NotSupportedException($"Unsupported statement type: {statement.GetType().Name}")
            },
            ImmutableArray<SecurityPredicateId>.Empty,
            CompilerInfo.Version);
    }

    /// <summary>
    /// Literals are bound instead of inlined on a bound emitter context (WP-A3, INV-4). A dialect opts in once its inline
    /// structural positions are registered with the emitter context. Off for dialects without a governed path.
    /// </summary>
    protected virtual bool BindLiterals => false;

    /// <summary>Appends a validated integer at an allow-listed inline position (row count, ordinal, frame offset) and registers it.</summary>
    protected static void AppendInlineInteger(ref ValueStringBuilder builder, long value, SqlEmitterContext context)
    {
        int start = builder.Length;
        builder.Append(value);
        context.RegisterInlineNumericPosition(start, builder.Length - start);
    }

    /// <summary>Appends reviewed constant template text (it may contain quotes and digits) and registers it as a constant fragment.</summary>
    protected static void AppendConstantFragment(ref ValueStringBuilder builder, SqlEmitterContext context, string text)
    {
        int start = builder.Length;
        builder.Append(text);
        context.RegisterConstantFragment(start, builder.Length - start);
    }

    /// <summary>Renders a typed column mask. Dialects without a governed path reject masks (fail closed).</summary>
    protected virtual void FormatMask(ref ValueStringBuilder builder, MaskExpression mask, SqlEmitterContext context) =>
        throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect);

    /// <summary>Appends constant generator text that may contain digits (types, fixed templates) and registers it as structural.</summary>
    protected static void AppendStructural(ref ValueStringBuilder builder, SqlEmitterContext context, string text)
    {
        int start = builder.Length;
        builder.Append(text);
        context.RegisterInlineNumericPosition(start, builder.Length - start);
    }

    /// <summary>
    /// Row counts and ordinals are structural integers that stay inline (plan 3.4); any other expression is emitted normally.
    /// </summary>
    protected void GenerateStructuralInteger(Expression expression, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (context.IsBound && BindLiterals && expression is LiteralExpression { Type: LiteralType.Integer, Value: not null } literal)
        {
            AppendInlineInteger(ref builder, Convert.ToInt64(literal.Value, CultureInfo.InvariantCulture), context);
            return;
        }

        GenerateExpression(expression, ref builder, context);
    }

    /// <summary>Binds an integer value with the narrowest fitting integer type.</summary>
    protected static string BindInteger(long value, SqlEmitterContext context) =>
        context.BindValue(value is >= int.MinValue and <= int.MaxValue ? (int)value : value,
            value is >= int.MinValue and <= int.MaxValue ? SqlParameterType.Int32 : SqlParameterType.Int64,
            ParameterOrigin.QueryLiteral);

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
        RuntimeHelpers.EnsureSufficientExecutionStack();
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

    /// <summary>A table-less SELECT needs a dummy source on some dialects (Oracle: DUAL).</summary>
    protected virtual string? FromlessSource => null;

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
        RuntimeHelpers.EnsureSufficientExecutionStack();
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
        // Wunsch 4: restore the outer projection context; a scalar subquery in a projection used to reset it to false,
        // so later boolean projections were no longer wrapped (SQL Server, Oracle).
        bool prevProjection = context.InProjectionContext;
        context.InPredicateContext = false;
        context.InProjectionContext = true;
        for (int i = 0; i < spec.Projections.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateSelectItem(spec.Projections[i], ref builder, context);
        }
        context.InProjectionContext = prevProjection;
        context.InPredicateContext = prevPred;

        if (spec.From != null)
        {
            builder.Append(" FROM ");
            GenerateTableSource(spec.From, ref builder, context);
        }
        else if (FromlessSource is { } dummy)
        {
            builder.Append(" FROM ");
            builder.Append(dummy);
        }

        if (spec.Where != null)
        {
            builder.Append(" WHERE ");
            bool prevWherePred = context.InPredicateContext;
            context.InPredicateContext = true;
            GenerateExpression(spec.Where, ref builder, context);
            context.InPredicateContext = prevWherePred;
        }

        if (spec.GroupBy != null && (spec.GroupBy.GroupingExpressions.Count > 0 || spec.GroupBy.AdvancedElements is { Count: > 0 }))
        {
            GenerateGroupBy(spec.GroupBy, ref builder, context);
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
        RuntimeHelpers.EnsureSufficientExecutionStack();
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
                    if (!SupportsJoinUsing)
                    {
                        // ON l.c = r.c is not equivalent (USING merges the column), so the construct is rejected.
                        throw UnsupportedConstruct("JOIN … USING", TargetDialect);
                    }

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
            GenerateStructuralInteger(el.Expression, ref builder, context);
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
        RuntimeHelpers.EnsureSufficientExecutionStack();
        context.Tick();
        switch (expression)
        {
            case MaskExpression mask:
                FormatMask(ref builder, mask, context);
                break;
            case SecurityPredicateExpression securityPredicate:
                // Always parenthesized: the injected predicate must never combine with neighbouring operators by precedence.
                builder.Append('(');
                GenerateExpression(securityPredicate.Predicate, ref builder, context);
                builder.Append(')');
                break;
            case PolicyParameterExpression policyParameter:
                if (!context.IsBound)
                {
                    throw new NotSupportedException("Policy parameters require a bound emitter context (use Generate).");
                }

                builder.Append(context.BindPolicy(policyParameter));
                break;
            case ColumnReference col:
                FormatQualifiedName(ref builder, col.Name, context);
                break;
            case ParenthesizedExpression paren:
                builder.Append('(');
                GenerateExpression(paren.Expression, ref builder, context);
                builder.Append(')');
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
                GeneratePredicateOperand(lk.Operand, ref builder, context);
                builder.Append(lk.IsNotLike ? " NOT LIKE " : " LIKE ");
                if (context.IsBound && lk.Escape == null && lk.Pattern is PolicyParameterExpression policyPattern)
                {
                    // SEC-ADG-18: LIKE semantics differ per dialect ([...] classes, default escapes). A policy pattern always has
                    // an explicit ESCAPE from a constant template, and structured values are escaped when bound.
                    builder.Append(context.BindPolicy(policyPattern, escapeLikePattern: !policyPattern.IsLikePattern));
                    builder.Append(" ESCAPE ");
                    int escapeStart = builder.Length;
                    builder.Append("'\\'");
                    context.RegisterConstantFragment(escapeStart, builder.Length - escapeStart);
                    break;
                }

                GeneratePredicateOperand(lk.Pattern, ref builder, context);
                if (lk.Escape != null)
                {
                    builder.Append(" ESCAPE ");
                    GeneratePredicateOperand(lk.Escape, ref builder, context);
                }
                break;
            case InListExpression inL:
                GeneratePredicateOperand(inL.Operand, ref builder, context);
                builder.Append(inL.IsNotIn ? " NOT IN (" : " IN (");
                for (int i = 0; i < inL.Items.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    GenerateExpression(inL.Items[i], ref builder, context);
                }
                builder.Append(')');
                break;
            case InSubqueryExpression inSq:
                GeneratePredicateOperand(inSq.Operand, ref builder, context);
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
                GeneratePredicateOperand(qc.Left, ref builder, context);
                builder.Append(' ');
                builder.Append(GetBinaryOperatorString(qc.Operator));
                builder.Append(' ');
                builder.Append(qc.Quantifier.ToString().ToUpperInvariant());
                builder.Append(" (");
                GenerateSelect(qc.Subquery, ref builder, context);
                builder.Append(')');
                break;
            case BetweenExpression bt:
                GeneratePredicateOperand(bt.Operand, ref builder, context);
                builder.Append(bt.IsNotBetween ? " NOT BETWEEN " : " BETWEEN ");
                GeneratePredicateOperand(bt.Lower, ref builder, context);
                builder.Append(" AND ");
                GeneratePredicateOperand(bt.Upper, ref builder, context);
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
                    // Wunsch 4: a searched CASE condition is a predicate, never a projected boolean (no second CASE wrap).
                    if (cs.Operand == null) GeneratePredicate(w.Condition, ref builder, context);
                    else GenerateExpression(w.Condition, ref builder, context);
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
                GenerateFunctionCall(fn, ref builder, context);
                break;
            case CurrentDateTimeExpression current:
                FormatCurrentDateTime(ref builder, current.Kind, context);
                break;
            case SubstringExpression substring:
                FormatSubstring(ref builder, substring, context);
                break;
            case TrimExpression trim:
                FormatTrim(ref builder, trim, context);
                break;
            case PositionExpression position:
                FormatPosition(ref builder, position, context);
                break;
            case GroupingOperationExpression grouping:
                FormatGroupingOperation(ref builder, grouping, context);
                break;
            case TypedLiteralExpression typed:
                FormatTypedLiteral(ref builder, typed, context);
                break;
            case IntervalLiteralExpression interval:
                FormatIntervalLiteral(ref builder, interval, context);
                break;
            case DateFunctionExpression date when date.Kind == DateFunctionKind.Add:
                FormatDateAdd(ref builder, date.Unit, date.Amount, date.Source, context);
                break;
            case DateFunctionExpression date:
                FormatDateTrunc(ref builder, date.Unit, date.Source, context);
                break;
            case CastExpression cast:
                if (cast.IsTryCast && !SupportsTryCast)
                {
                    throw UnsupportedConstruct("TRY_CAST", TargetDialect);
                }

                builder.Append(cast.IsTryCast ? "TRY_CAST(" : "CAST(");
                GenerateExpression(cast.Operand, ref builder, context);
                builder.Append(" AS ");
                AppendStructural(ref builder, context, FormatTypeName(ParseTypeName(cast.TargetType)));
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
                bool targetNeedsParens = sub.Target is BinaryExpression
                    or UnaryExpression
                    or BetweenExpression
                    or LikeExpression
                    or InListExpression
                    or InSubqueryExpression
                    or IsDistinctFromExpression
                    or CastExpression;
                if (targetNeedsParens) builder.Append('(');
                GenerateExpression(sub.Target, ref builder, context);
                if (targetNeedsParens) builder.Append(')');
                builder.Append('[');
                GenerateExpression(sub.Index, ref builder, context);
                builder.Append(']');
                break;
            case TrustedSqlExpression trusted:
                // Only used as a projection item (column masks), so no precedence parentheses are needed.
                builder.Append(trusted.Sql);
                break;
            case ExtractExpression ext:
                FormatExtract(ref builder, CanonicalExtractField(ext.Field), ext.Source, context);
                break;
            default:
                throw new NotSupportedException($"Unsupported expression: {expression.GetType().Name}");
        }
    }

    protected virtual void FormatBinaryExpression(ref ValueStringBuilder builder, BinaryExpression b, SqlEmitterContext context)
    {
        // The canonical tautology and deny-all (1 = 1, 1 = 0) are the only constants emitted as text (plan 3.4).
        if (context.IsBound && BindLiterals && b.Operator == BinaryOperator.Equal &&
            b.Left is LiteralExpression { Type: LiteralType.Integer, Value: not null } left &&
            b.Right is LiteralExpression { Type: LiteralType.Integer, Value: not null } right &&
            Convert.ToInt64(left.Value, CultureInfo.InvariantCulture) == 1 &&
            Convert.ToInt64(right.Value, CultureInfo.InvariantCulture) is 0 or 1)
        {
            AppendStructural(ref builder, context, Convert.ToInt64(right.Value, CultureInfo.InvariantCulture) == 1 ? "1 = 1" : "1 = 0");
            return;
        }

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
        if (context.IsBound)
        {
            builder.Append(context.BindClient(param));
            return;
        }

        // Wunsch 4: a client parameter (@name → __param_name) stays a named placeholder. The caller binds by name and
        // restores @name after the rewrite (GovernedSqlExecutionService.RestoreClientParameters); a positional marker
        // ($1, @p0, ?1) could not be bound, because no mapping is returned.
        if (param.IsSynthetic && SafeParamIdentifierRegex.IsMatch(param.Name))
        {
            builder.Append("__param_");
            builder.Append(param.Name);
            return;
        }

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
                if (u.Operand is BinaryExpression or BetweenExpression or LikeExpression or InListExpression or InSubqueryExpression or IsDistinctFromExpression)
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
                if (u.Operand is BinaryExpression or BetweenExpression or LikeExpression or InListExpression or InSubqueryExpression or IsDistinctFromExpression)
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

    protected virtual void GeneratePredicateOperand(Expression expr, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        bool needsParens = expr is BinaryExpression
            or BetweenExpression
            or LikeExpression
            or InListExpression
            or InSubqueryExpression
            or IsDistinctFromExpression
            or UnaryExpression;

        if (needsParens) builder.Append('(');
        GenerateExpression(expr, ref builder, context);
        if (needsParens) builder.Append(')');
    }

    protected virtual void FormatIsDistinctFrom(ref ValueStringBuilder builder, IsDistinctFromExpression dist, SqlEmitterContext context)
    {
        GeneratePredicateOperand(dist.Left, ref builder, context);
        builder.Append(dist.IsNotDistinctFrom ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ");
        GeneratePredicateOperand(dist.Right, ref builder, context);
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

        if (child is BetweenExpression)
        {
            if (parentOp is BinaryOperator.And or BinaryOperator.Or) return true;
            return GetPrecedence(parentOp) >= 3;
        }

        if (child is LikeExpression or InListExpression or InSubqueryExpression or IsDistinctFromExpression)
        {
            return GetPrecedence(parentOp) >= 3;
        }

        if (child is UnaryExpression u)
        {
            if (u.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull)
                return GetPrecedence(parentOp) >= 3;
            if (u.Operator is UnaryOperator.Not)
                return GetPrecedence(parentOp) > 2;
            return false;
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

    /// <summary>
    /// Wunsch 4: Trino EXTRACT field in canonical form (DOW/ISODOW → DAY_OF_WEEK, DOY → DAY_OF_YEAR,
    /// YOW/ISOYEAR → YEAR_OF_WEEK, DAY_OF_MONTH → DAY). Trino's DAY_OF_WEEK is ISO: Monday = 1 … Sunday = 7.
    /// </summary>
    protected static string CanonicalExtractField(string field) =>
        TrinoSqlEngine.Ast.SqlSafeTokens.EnsureExtractField(field) switch
        {
            "DOW" or "ISODOW" => "DAY_OF_WEEK",
            "DOY" => "DAY_OF_YEAR",
            "YOW" or "ISOYEAR" => "YEAR_OF_WEEK",
            "DAY_OF_MONTH" => "DAY",
            var f => f
        };

    protected static TrinoSqlEngine.Ast.Builder.AstBuildException UnsupportedExtract(string field, TargetSqlDialect dialect) =>
        new($"SQL construct EXTRACT({field} FROM …) is not supported for {dialect}.");

    /// <summary>
    /// Wunsch 4: EXTRACT in PostgreSQL/DuckDB naming (ISODOW, DOY, ISOYEAR); PostgreSQL's DOW would count Sunday = 0.
    /// </summary>
    protected virtual void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        builder.Append("EXTRACT(");
        builder.Append(field switch
        {
            "DAY_OF_WEEK" => "ISODOW",
            "DAY_OF_YEAR" => "DOY",
            "YEAR_OF_WEEK" => "ISOYEAR",
            var f => f
        });
        builder.Append(" FROM ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: the dialect accepts <c>JOIN … USING (…)</c> (all but SQL Server).</summary>
    protected virtual bool SupportsJoinUsing => true;

    /// <summary>Wunsch 4: the dialect has TRY_CAST (SQL Server, DuckDB, Snowflake).</summary>
    protected virtual bool SupportsTryCast => false;

    /// <summary>A validated Trino type: lower-case base name (e.g. <c>timestamp</c>), argument list without spaces, time zone flag.</summary>
    protected readonly record struct TrinoType(string Name, string? Arguments, bool WithTimeZone, string Normalized);

    private static readonly Regex TypeNameParts = new(@"^(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?<args>\([0-9, ]+\))?(?<tz> with time zone)?$", RegexOptions.CultureInvariant);

    protected static TrinoType ParseTypeName(string type)
    {
        // SQL-3: only plain tokens reach the target database.
        string normalized = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(type);
        string lower = normalized.ToLowerInvariant();
        var match = TypeNameParts.Match(lower);
        if (!match.Success)
        {
            return new TrinoType(lower, null, false, normalized);
        }

        string? args = match.Groups["args"].Success ? match.Groups["args"].Value.Replace(" ", string.Empty, StringComparison.Ordinal) : null;
        return new TrinoType(match.Groups["name"].Value, args, match.Groups["tz"].Success, normalized);
    }

    /// <summary>
    /// Wunsch 4: CAST target type in the dialect's spelling. The default keeps the (validated) Trino spelling, which is valid
    /// for SQLite, DuckDB, Snowflake and ANSI.
    /// </summary>
    protected virtual string FormatTypeName(TrinoType type) => type.Normalized;

    protected static TrinoSqlEngine.Ast.Builder.AstBuildException UnsupportedConstruct(string construct, TargetSqlDialect dialect) =>
        new($"SQL construct {construct} is not supported for {dialect}.");

    /// <summary>Wunsch 4: ANSI <c>CURRENT_DATE</c> … <c>LOCALTIMESTAMP</c>.</summary>
    protected virtual void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CURRENT_DATE",
            CurrentDateTimeKind.CurrentTime => "CURRENT_TIME",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
            CurrentDateTimeKind.LocalTime => "LOCALTIME",
            _ => "LOCALTIMESTAMP"
        });
    }

    protected virtual string SubstringFunctionName => "SUBSTRING";

    /// <summary>Wunsch 4: <c>SUBSTRING(x, start[, length])</c>.</summary>
    protected virtual void FormatSubstring(ref ValueStringBuilder builder, SubstringExpression substring, SqlEmitterContext context)
    {
        builder.Append(SubstringFunctionName);
        builder.Append('(');
        GenerateExpression(substring.Source, ref builder, context);
        builder.Append(", ");
        GenerateExpression(substring.Start, ref builder, context);
        if (substring.Length != null)
        {
            builder.Append(", ");
            GenerateExpression(substring.Length, ref builder, context);
        }
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>TRIM(BOTH|LEADING|TRAILING [chars] FROM x)</c>.</summary>
    protected virtual void FormatTrim(ref ValueStringBuilder builder, TrimExpression trim, SqlEmitterContext context)
    {
        builder.Append(trim.Specification switch
        {
            TrimSpecification.Leading => "TRIM(LEADING ",
            TrimSpecification.Trailing => "TRIM(TRAILING ",
            _ => "TRIM(BOTH "
        });
        if (trim.Characters != null)
        {
            GenerateExpression(trim.Characters, ref builder, context);
            builder.Append(' ');
        }
        builder.Append("FROM ");
        GenerateExpression(trim.Source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>POSITION(needle IN haystack)</c>.</summary>
    protected virtual void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context)
    {
        builder.Append("POSITION(");
        GenerateExpression(position.Needle, ref builder, context);
        builder.Append(" IN ");
        GenerateExpression(position.Haystack, ref builder, context);
        builder.Append(')');
    }

    /// <summary>INSTR(haystack, needle), used by SQLite and Oracle for POSITION.</summary>
    protected void FormatInstr(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context)
    {
        builder.Append("INSTR(");
        GenerateExpression(position.Haystack, ref builder, context);
        builder.Append(", ");
        GenerateExpression(position.Needle, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: the dialect supports ROLLUP, CUBE and GROUPING SETS.</summary>
    protected virtual bool SupportsGroupingSets => true;

    /// <summary>Wunsch 4: the dialect supports <c>GROUP BY DISTINCT</c>.</summary>
    protected virtual bool SupportsGroupByDistinct => false;

    protected virtual void GenerateGroupBy(GroupByClause groupBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append(" GROUP BY ");
        if (groupBy.Distinct)
        {
            if (!SupportsGroupByDistinct)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUP BY DISTINCT is not supported for {TargetDialect}.");
            }

            builder.Append("DISTINCT ");
        }

        bool first = true;
        foreach (var expr in groupBy.GroupingExpressions)
        {
            if (!first) builder.Append(", ");
            GenerateStructuralInteger(expr, ref builder, context);
            first = false;
        }

        foreach (var element in groupBy.AdvancedElements ?? [])
        {
            if (!SupportsGroupingSets)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUP BY {element.Kind} is not supported for {TargetDialect}.");
            }

            if (!first) builder.Append(", ");
            first = false;
            builder.Append(element.Kind switch
            {
                GroupingElementKind.Rollup => "ROLLUP (",
                GroupingElementKind.Cube => "CUBE (",
                _ => "GROUPING SETS ("
            });
            for (int i = 0; i < element.Sets.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                var set = element.Sets[i];
                // GROUPING SETS lists each set in parentheses; ROLLUP/CUBE only composite sets.
                bool parenthesize = element.Kind == GroupingElementKind.GroupingSets || set.Count != 1;
                if (parenthesize) builder.Append('(');
                for (int j = 0; j < set.Count; j++)
                {
                    if (j > 0) builder.Append(", ");
                    GenerateStructuralInteger(set[j], ref builder, context);
                }
                if (parenthesize) builder.Append(')');
            }
            builder.Append(')');
        }
    }

    /// <summary>Wunsch 4: <c>GROUPING(a, b)</c>; several columns give a bitmask in Trino, PostgreSQL and Oracle.</summary>
    protected virtual void FormatGroupingOperation(ref ValueStringBuilder builder, GroupingOperationExpression grouping, SqlEmitterContext context)
    {
        if (!SupportsGroupingSets)
        {
            throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct GROUPING() is not supported for {TargetDialect}.");
        }

        builder.Append("GROUPING(");
        for (int i = 0; i < grouping.Columns.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateExpression(grouping.Columns[i], ref builder, context);
        }
        builder.Append(')');
    }

    /// <summary>Wunsch 4: the dialect evaluates <c>agg(…) FILTER (WHERE …)</c> natively; otherwise it is emulated with CASE.</summary>
    protected virtual bool SupportsAggregateFilter => false;

    /// <summary>Wunsch 4: the dialect accepts <c>ORDER BY</c> inside an aggregate's argument list.</summary>
    protected virtual bool SupportsOrderedAggregates => false;

    protected virtual void GenerateFunctionCall(FunctionCallExpression fn, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        FormatFunctionName(ref builder, fn.Name, context);
        builder.Append('(');
        if (fn.Distinct) builder.Append("DISTINCT ");

        bool emulateFilter = fn.Filter != null && !SupportsAggregateFilter;
        if (emulateFilter)
        {
            // agg(x) FILTER (WHERE c) == agg(CASE WHEN c THEN x END): aggregates ignore NULL; COUNT(*) counts 1 per row.
            if (!fn.IsStar && fn.Arguments.Count != 1)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException(
                    $"SQL construct FILTER (WHERE …) on {fn.Name.NormalizedName} with {fn.Arguments.Count} arguments is not supported for {TargetDialect}.");
            }

            builder.Append("CASE WHEN ");
            GeneratePredicate(fn.Filter!, ref builder, context);
            builder.Append(" THEN ");
            if (fn.IsStar) builder.Append('1');
            else GenerateExpression(fn.Arguments[0], ref builder, context);
            builder.Append(" END");
        }
        else
        {
            if (fn.IsStar) builder.Append('*');
            for (int i = 0; i < fn.Arguments.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                GenerateExpression(fn.Arguments[i], ref builder, context);
            }
        }

        if (fn.OrderWithin != null)
        {
            if (!SupportsOrderedAggregates)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException(
                    $"SQL construct ORDER BY inside {fn.Name.NormalizedName}(…) is not supported for {TargetDialect}.");
            }

            builder.Append(' ');
            GenerateOrderBy(fn.OrderWithin, ref builder, context);
        }

        builder.Append(')');

        if (fn.Filter != null && !emulateFilter)
        {
            builder.Append(" FILTER (WHERE ");
            GeneratePredicate(fn.Filter, ref builder, context);
            builder.Append(')');
        }

        if (fn.Window != null)
        {
            GenerateWindow(fn.Window, ref builder, context);
        }
    }

    private void GenerateWindow(WindowSpecification window, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append(" OVER (");
        bool needsSpace = false;
        if (window.PartitionBy != null && window.PartitionBy.Count > 0)
        {
            builder.Append("PARTITION BY ");
            for (int i = 0; i < window.PartitionBy.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                GenerateExpression(window.PartitionBy[i], ref builder, context);
            }
            needsSpace = true;
        }

        if (window.OrderBy != null)
        {
            if (needsSpace) builder.Append(' ');
            GenerateOrderBy(window.OrderBy, ref builder, context);
            needsSpace = true;
        }

        if (window.Frame != null)
        {
            if (needsSpace) builder.Append(' ');
            builder.Append(window.Frame.Type == WindowFrameType.Rows ? "ROWS " : "RANGE ");
            if (window.Frame.End != null)
            {
                builder.Append("BETWEEN ");
                FormatFrameBound(ref builder, window.Frame.Start, context);
                builder.Append(" AND ");
                FormatFrameBound(ref builder, window.Frame.End, context);
            }
            else
            {
                FormatFrameBound(ref builder, window.Frame.Start, context);
            }
        }

        builder.Append(')');
    }

    private static void FormatFrameBound(ref ValueStringBuilder builder, FrameBound bound, SqlEmitterContext context)
    {
        switch (bound.Kind)
        {
            case FrameBoundKind.UnboundedPreceding: builder.Append("UNBOUNDED PRECEDING"); break;
            case FrameBoundKind.UnboundedFollowing: builder.Append("UNBOUNDED FOLLOWING"); break;
            case FrameBoundKind.CurrentRow: builder.Append("CURRENT ROW"); break;
            case FrameBoundKind.Preceding:
                AppendInlineInteger(ref builder, bound.Offset, context);
                builder.Append(" PRECEDING");
                break;
            case FrameBoundKind.Following:
                AppendInlineInteger(ref builder, bound.Offset, context);
                builder.Append(" FOLLOWING");
                break;
        }
    }

    /// <summary>
    /// A boolean condition (CASE WHEN, FILTER): generated outside the projection context, so dialects that wrap predicates
    /// in projections (SQL Server, Oracle) do not wrap it a second time.
    /// </summary>
    protected void GeneratePredicate(Expression predicate, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        bool prevProjection = context.InProjectionContext;
        bool prevPredicate = context.InPredicateContext;
        context.InProjectionContext = false;
        context.InPredicateContext = true;
        GenerateExpression(predicate, ref builder, context);
        context.InProjectionContext = prevProjection;
        context.InPredicateContext = prevPredicate;
    }

    /// <summary>Wunsch 4: ANSI <c>DATE '…'</c>, <c>TIME '…'</c>, <c>TIMESTAMP '…'</c>.</summary>
    protected virtual void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => "DATE ",
            TypedLiteralKind.Time => "TIME ",
            _ => "TIMESTAMP "
        });
        FormatStringLiteral(ref builder, literal.Value, context);
    }

    protected static TrinoSqlEngine.Ast.Builder.AstBuildException UnsupportedDateFunction(string function, DateUnit unit, TargetSqlDialect dialect) =>
        new($"SQL construct {function}('{DateUnitName(unit)}', …) is not supported for {dialect}.");

    /// <summary>Lower-case Trino name of <paramref name="unit"/> (<c>day</c>).</summary>
    protected static string DateUnitName(DateUnit unit) => unit switch
    {
        DateUnit.Second => "second",
        DateUnit.Minute => "minute",
        DateUnit.Hour => "hour",
        DateUnit.Day => "day",
        DateUnit.Week => "week",
        DateUnit.Month => "month",
        _ => "year"
    };

    /// <summary>
    /// Virtual filters (phase 7b): ANSI <c>(x ± INTERVAL 'n' UNIT)</c>; a week is seven days (ANSI has no WEEK field).
    /// </summary>
    protected virtual void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        (long value, string field) = unit == DateUnit.Week ? (amount * 7, "DAY") : (amount, DateUnitName(unit).ToUpperInvariant());
        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(value < 0 ? " - INTERVAL '" : " + INTERVAL '");
        builder.Append(Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("' ");
        builder.Append(field);
        builder.Append(')');
    }

    /// <summary>Virtual filters (phase 7b): <c>DATE_TRUNC('unit', x)</c> (PostgreSQL, DuckDB, Snowflake, ANSI fallback).</summary>
    protected virtual void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        builder.Append("DATE_TRUNC('");
        builder.Append(DateUnitName(unit));
        builder.Append("', ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: ANSI <c>INTERVAL '…' FIELD</c>.</summary>
    protected virtual void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        builder.Append("INTERVAL ");
        FormatStringLiteral(ref builder, interval.Value, context);
        builder.Append(' ');
        builder.Append(interval.Field);
    }

    /// <summary>
    /// Wunsch 4: a function name is not an identifier. Quoting it (<c>"coalesce"</c>, <c>[SUM]</c>) makes PostgreSQL and
    /// SQL Server look for a user-defined object, so built-ins fail. An unquoted one-part name (it passed
    /// <see cref="SqlFunctionPolicy"/> in the builder) is emitted bare and upper-case; quoted or qualified names keep
    /// delimited parts.
    /// </summary>
    public virtual void FormatFunctionName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        if (name.Parts.Count == 1 && !name.Parts[0].IsQuoted && BareFunctionName.IsMatch(name.Parts[0].Value))
        {
            builder.Append(name.Parts[0].Value.ToUpperInvariant());
            return;
        }

        FormatQualifiedName(ref builder, name, context);
    }

    private static readonly Regex BareFunctionName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

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

        if (context.IsBound && BindLiterals)
        {
            FormatBoundLiteral(ref builder, lit, context);
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

    /// <summary>INV-4: a literal value reaches the database only as a bound parameter.</summary>
    private void FormatBoundLiteral(ref ValueStringBuilder builder, LiteralExpression lit, SqlEmitterContext context)
    {
        switch (lit.Type)
        {
            case LiteralType.Boolean:
            {
                // TRUE and FALSE are keywords (plan 3.4); the dialect spells them as a registered structural token.
                int start = builder.Length;
                FormatBoolean(ref builder, (bool)lit.Value!, context);
                context.RegisterInlineNumericPosition(start, builder.Length - start);
                break;
            }
            case LiteralType.Integer:
                builder.Append(BindInteger(Convert.ToInt64(lit.Value, CultureInfo.InvariantCulture), context));
                break;
            case LiteralType.Decimal:
            {
                decimal value = lit.Value switch
                {
                    decimal d => d,
                    double dbl => (decimal)dbl,
                    _ => decimal.Parse(Convert.ToString(lit.Value, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture)
                };
                builder.Append(context.BindValue(value, SqlParameterType.Decimal, ParameterOrigin.QueryLiteral));
                break;
            }
            case LiteralType.String:
                builder.Append(context.BindValue(lit.Value!.ToString() ?? string.Empty, SqlParameterType.String, ParameterOrigin.QueryLiteral));
                break;
            case LiteralType.Binary:
                builder.Append(context.BindValue(ParseBinaryLiteral(lit.Value!.ToString() ?? string.Empty), SqlParameterType.Binary, ParameterOrigin.QueryLiteral));
                break;
            default:
                throw new NotSupportedException($"Unsupported literal type: {lit.Type}");
        }
    }

    private static byte[] ParseBinaryLiteral(string literal)
    {
        string checkedLiteral = TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(literal);
        string hex = checkedLiteral[2..^1].Replace(" ", string.Empty, StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }

    /// <summary>Parses the value of a DATE/TIME/TIMESTAMP literal into a typed .NET value; unparsable values fail closed.</summary>
    protected static (object Value, SqlParameterType Type) ParseTypedLiteralValue(TypedLiteralExpression literal)
    {
        string text = literal.Value.Trim();
        switch (literal.Kind)
        {
            case TypedLiteralKind.Date when DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return (date.ToDateTime(TimeOnly.MinValue), SqlParameterType.Date);
            case TypedLiteralKind.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time):
                return (time.ToTimeSpan(), SqlParameterType.Time);
            case TypedLiteralKind.Timestamp when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp):
                return (timestamp, SqlParameterType.Timestamp);
            default:
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"The {literal.Kind} literal value is not a valid {literal.Kind} value.");
        }
    }
}
