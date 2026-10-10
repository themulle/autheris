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
public abstract partial class SqlDialectGeneratorBase : ISqlDialectGenerator
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
#if DEBUG
            sql = TrinoSqlEngine.CompilerTestSeams.Current?.FaultyEmitter?.Invoke(sql) ?? sql;
#endif
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

}
