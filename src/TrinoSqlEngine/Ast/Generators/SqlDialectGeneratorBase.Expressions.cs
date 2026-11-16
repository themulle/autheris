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
        ExecutionStackGuard.Ensure();
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

                builder.Append(context.BindPolicy(policyParameter, escapeLikePattern: context.EmittingLikePattern && !policyParameter.IsLikePattern));
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
                if (context.IsBound && lk.Escape == null && LikePatternEscaper.ContainsPolicyParameter(lk.Pattern))
                {
                    // 18 / SQLE-09: LIKE semantics differ per dialect ([...] classes, default escapes). A policy pattern always has
                    // an explicit ESCAPE from a constant template, and every declared value inside the pattern (also in a || chain) is escaped
                    // when bound; the policy parser admits only literals, declared parameters and || in a pattern.
                    bool previous = context.EmittingLikePattern;
                    context.EmittingLikePattern = true;
                    try
                    {
                        GeneratePredicateOperand(lk.Pattern, ref builder, context);
                    }
                    finally
                    {
                        context.EmittingLikePattern = previous;
                    }

                    builder.Append(" ESCAPE ");
                    int escapeStart = builder.Length;
                    builder.Append(LikeEscapeLiteral());
                    context.RegisterConstantFragment(escapeStart, builder.Length - escapeStart);
                    break;
                }

                var normalized = LikePatternEscaper.NormalizeUserLike(lk, TargetDialect, maxQueryLength: null);
                if (normalized.IsGatewayOwnedPattern && normalized.Escape != null)
                {
                    // SQL Server: '[' in the bound value is a character class; prefixing it with the clause's escape character makes it literal.
                    // REPLACE keeps the type of its first argument: CAST to nvarchar(max) so doubled brackets never truncate the value.
                    builder.Append("REPLACE(CAST(");
                    GeneratePredicateOperand(normalized.Pattern, ref builder, context);
                    builder.Append(" AS nvarchar(max)), ");
                    int bracketStart = builder.Length;
                    builder.Append("'['");
                    context.RegisterConstantFragment(bracketStart, builder.Length - bracketStart);
                    builder.Append(", ");
                    GeneratePredicateOperand(normalized.Escape, ref builder, context);
                    builder.Append(" + ");
                    bracketStart = builder.Length;
                    builder.Append("'['");
                    context.RegisterConstantFragment(bracketStart, builder.Length - bracketStart);
                    builder.Append(')');
                }
                else
                {
                    GeneratePredicateOperand(normalized.Pattern, ref builder, context);
                }

                if (normalized.Escape != null)
                {
                    builder.Append(" ESCAPE ");
                    GeneratePredicateOperand(normalized.Escape, ref builder, context);
                }
                else if (TargetDialect is TargetSqlDialect.PostgreSql or TargetSqlDialect.Databricks)
                {
                    builder.Append(" ESCAPE ");
                    int escapeStart = builder.Length;
                    builder.Append(normalized.Pattern is LiteralExpression { Type: LiteralType.String } || TargetDialect == TargetSqlDialect.Databricks ? LikeEscapeLiteral() : "''");
                    context.RegisterConstantFragment(escapeStart, builder.Length - escapeStart);
                }
                else if (TargetDialect == TargetSqlDialect.SqlServer)
                {
                    builder.Append(" ESCAPE ");
                    int escapeStart = builder.Length;
                    builder.Append(LikeEscapeLiteral());
                    context.RegisterConstantFragment(escapeStart, builder.Length - escapeStart);
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

                if (TryFormatCast(ref builder, cast, context))
                {
                    break;
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
            case NamedBindMarkerExpression marker:
                if (context.IsBound)
                {
                    throw new NotSupportedException("Named bind markers are only emitted by the inline text rendering.");
                }

                builder.Append('@');
                builder.Append(marker.Name);
                break;
            case ExtractExpression ext:
                FormatExtract(ref builder, CanonicalExtractField(ext.Field), ext.Source, context);
                break;
            default:
                throw new NotSupportedException($"Unsupported expression: {expression.GetType().Name}");
        }
    }

    protected virtual bool RequiresCustomBinaryFormat(BinaryExpression b) => false;

    protected void FormatStandardBinary(
        ref ValueStringBuilder builder,
        BinaryExpression b,
        SqlEmitterContext context,
        string? operatorOverride = null)
    {
        bool parensLeft = NeedsParentheses(b.Left, b.Operator, isLeft: true);
        bool parensRight = NeedsParentheses(b.Right, b.Operator, isLeft: false);

        if (parensLeft) builder.Append('(');
        GenerateExpression(b.Left, ref builder, context);
        if (parensLeft) builder.Append(')');

        builder.Append(' ');
        builder.Append(operatorOverride ?? GetBinaryOperatorString(b.Operator));
        builder.Append(' ');

        if (parensRight) builder.Append('(');
        GenerateExpression(b.Right, ref builder, context);
        if (parensRight) builder.Append(')');
    }

    /// <summary>The ESCAPE literal for the backslash escape character. Spark interprets backslash escapes inside quoted literals, so it needs the doubled form.</summary>
    private string LikeEscapeLiteral() => TargetDialect == TargetSqlDialect.Databricks ? "'\\\\'" : "'\\'";

    protected virtual void FormatBinaryExpression(ref ValueStringBuilder builder, BinaryExpression b, SqlEmitterContext context)
    {
        if (b.Left is not BinaryExpression)
        {
            if (TryAppendCanonicalConstant(ref builder, b, context))
            {
                return;
            }

            FormatStandardBinary(ref builder, b, context);
            return;
        }

        // One reusable frame stack per thread: a nested chain (GenerateExpression of a right operand) pushes above this call's frames
        // and removes its own before it returns, so the entries below stay intact and no list is allocated per chain.
        var frames = t_binaryFrames ??= [];
        int start = frames.Count;
        try
        {
            Expression cur = b;
            while (cur is BinaryExpression cb && !RequiresCustomBinaryFormat(cb))
            {
                if (frames.Count > start)
                {
                    ExecutionStackGuard.Ensure();
                    context.Tick();
                }

                if (TryAppendCanonicalConstant(ref builder, cb, context))
                {
                    cur = null!;
                    break;
                }

                bool parensLeft = NeedsParentheses(cb.Left, cb.Operator, isLeft: true);
                bool parensRight = NeedsParentheses(cb.Right, cb.Operator, isLeft: false);
                if (parensLeft) builder.Append('(');
                frames.Add((cb, parensLeft, parensRight));
                cur = cb.Left;
            }

            if (cur is not null)
            {
                GenerateExpression(cur, ref builder, context);
            }

            for (int i = frames.Count - 1; i >= start; i--)
            {
                var (node, parensLeft, parensRight) = frames[i];
                // Drop this frame before descending: a nested call must see only the frames below its own start.
                frames.RemoveAt(i);
                if (parensLeft) builder.Append(')');

                builder.Append(' ');
                builder.Append(GetBinaryOperatorString(node.Operator));
                builder.Append(' ');

                if (parensRight) builder.Append('(');
                GenerateExpression(node.Right, ref builder, context);
                if (parensRight) builder.Append(')');
            }
        }
        finally
        {
            if (frames.Count > start)
            {
                frames.RemoveRange(start, frames.Count - start);
            }
        }
    }

    [ThreadStatic]
    private static List<(BinaryExpression Node, bool ParensLeft, bool ParensRight)>? t_binaryFrames;

    private bool TryAppendCanonicalConstant(ref ValueStringBuilder builder, BinaryExpression b, SqlEmitterContext context)
    {
        // The canonical tautology and deny-all (1 = 1, 1 = 0) are the only constants emitted as text (plan 3.4).
        if (context.IsBound && BindLiterals && b.Operator == BinaryOperator.Equal &&
            b.Left is LiteralExpression { Type: LiteralType.Integer, Value: not null } left &&
            b.Right is LiteralExpression { Type: LiteralType.Integer, Value: not null } right &&
            Convert.ToInt64(left.Value, CultureInfo.InvariantCulture) == 1 &&
            Convert.ToInt64(right.Value, CultureInfo.InvariantCulture) is 0 or 1)
        {
            AppendStructural(ref builder, context, Convert.ToInt64(right.Value, CultureInfo.InvariantCulture) == 1 ? "1 = 1" : "1 = 0");
            return true;
        }

        return false;
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

        if (!string.IsNullOrWhiteSpace(param.Name) && param.Name != "?" && !param.Name.StartsWith('$', StringComparison.Ordinal) && !param.IsSynthetic)
        {
            var rawName = param.Name.TrimStart('@', ':');
            if (SafeParamIdentifierRegex.IsMatch(rawName))
            {
                switch (context.Dialect)
                {
                    case TargetSqlDialect.SqlServer:
                        builder.Append('@');
                        builder.Append(rawName);
                        return;
                    case TargetSqlDialect.Oracle:
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

    private bool NeedsParentheses(Expression child, BinaryOperator parentOp, bool isLeft)
    {
        // Concatenation operands that are arithmetic or unary are always parenthesized, in every dialect: the operand keeps its
        // own evaluation order and type coercion whatever the target ranks '||' (or SQL Server '+') against the arithmetic operators.
        if (parentOp == BinaryOperator.Concat
            && (child is UnaryExpression
                || child is BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo }))
        {
            return true;
        }

        if (child is BinaryExpression childBinary)
        {
            int parentPrec = GetPrecedence(parentOp);
            int childPrec = GetPrecedence(childBinary.Operator);
            if (childPrec < parentPrec) return true;
            if (childPrec == parentPrec)
            {
                // SQLE-05: a comparison of a comparison is never chained (also on the left); a right operand of equal precedence is
                // parenthesized unless the operator is the associative AND / OR chain (no noise for those).
                if (childPrec == ComparisonPrecedence) return true;
                if (!isLeft && !(parentOp == childBinary.Operator && parentOp is BinaryOperator.And or BinaryOperator.Or)) return true;
            }

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

    private const int ComparisonPrecedence = 3;

    /// <summary>Binding strength of a binary operator; a dialect overrides it where its operator text differs (SQL Server <c>+</c> concatenation).</summary>
    protected virtual int GetPrecedence(BinaryOperator op) => op switch
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
