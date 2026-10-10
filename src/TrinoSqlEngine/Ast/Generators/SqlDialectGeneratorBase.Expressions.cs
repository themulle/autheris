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

namespace TrinoSqlEngine.Ast.Generators;

public abstract partial class SqlDialectGeneratorBase : ISqlDialectGenerator
{
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
                AppendStructural(ref builder, context, cast.IsNativeType ? NativeTypeName(cast) : FormatTypeName(ParseTypeName(cast.TargetType)));
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
}
