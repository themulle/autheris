namespace TrinoSqlEngine.Ast.Nodes;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;

public abstract record Expression : SqlNode;

public sealed record ColumnReference(SqlQualifiedName Name) : Expression;

/// <summary>
/// SQL-4: an expression rendered by the gateway itself for the target dialect (column mask expressions with bound key
/// parameters, e.g. <c>ENCODE(HMAC(..., @key, 'sha256'), 'hex')</c> or T-SQL <c>HASHBYTES</c>). It is emitted verbatim,
/// as the legacy rewriter does. The AST builder never creates it, so client SQL can never produce this node.
/// </summary>
public sealed record TrustedSqlExpression(string Sql) : Expression;

/// <summary>
/// A value bound by the gateway (tenant, policy, mask argument). It never originates from request text; the value comes from
/// the <see cref="ParameterSource"/> at emission time, so the AST (and a cached template) stays value-free.
/// </summary>
public sealed record PolicyParameterExpression(
    string Name,
    SqlParameterType Type,
    ParameterOrigin Origin = ParameterOrigin.Policy) : Expression;

public sealed record ParameterReference(
    string Name,
    int? PositionalIndex = null,
    bool IsSynthetic = false) : Expression;

public sealed record LiteralExpression(
    object? Value,
    LiteralType Type) : Expression;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names")]
public enum LiteralType
{
    Null,
    Boolean,
    Integer,
    Decimal,
    String,
    Binary
}

public sealed record ParenthesizedExpression(Expression Expression) : Expression;

public sealed record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right) : Expression;

public enum BinaryOperator
{
    // Logical
    And,
    Or,
    // Comparison
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    // Arithmetic & String
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Concat
}

public sealed record UnaryExpression(
    UnaryOperator Operator,
    Expression Operand) : Expression;

public enum UnaryOperator
{
    Not,
    Negate,
    IsNull,
    IsNotNull
}

public sealed record LikeExpression(
    Expression Operand,
    Expression Pattern,
    Expression? Escape = null,
    bool IsNotLike = false) : Expression;

public sealed record InListExpression(
    Expression Operand,
    IReadOnlyList<Expression> Items,
    bool IsNotIn) : Expression;

public sealed record InSubqueryExpression(
    Expression Operand,
    SelectStatement Subquery,
    bool IsNotIn) : Expression;

public sealed record ExistsExpression(
    SelectStatement Subquery) : Expression;

public sealed record ScalarSubqueryExpression(
    SelectStatement Subquery) : Expression;

public sealed record QuantifiedComparisonExpression(
    Expression Left,
    BinaryOperator Operator,
    ComparisonQuantifier Quantifier,
    SelectStatement Subquery) : Expression;

public enum ComparisonQuantifier
{
    Any,
    All,
    Some
}

public sealed record IsDistinctFromExpression(
    Expression Left,
    Expression Right,
    bool IsNotDistinctFrom) : Expression;

public sealed record BetweenExpression(
    Expression Operand,
    Expression Lower,
    Expression Upper,
    bool IsNotBetween) : Expression;

public sealed record CaseExpression(
    Expression? Operand,
    IReadOnlyList<WhenClause> WhenClauses,
    Expression? ElseResult) : Expression;

public sealed record WhenClause(
    Expression Condition,
    Expression Result) : SqlNode;

/// <param name="IsStar">Wunsch 4: the call takes <c>*</c> as its only argument (<c>COUNT(*)</c>); <see cref="Arguments"/> is empty.</param>
/// <param name="Filter">Wunsch 4: aggregate <c>FILTER (WHERE …)</c>.</param>
/// <param name="OrderWithin">Wunsch 4: <c>ORDER BY</c> inside the argument list of an aggregate (<c>array_agg(x ORDER BY y)</c>).</param>
public sealed record FunctionCallExpression(
    SqlQualifiedName Name,
    IReadOnlyList<Expression> Arguments,
    bool Distinct = false,
    WindowSpecification? Window = null,
    bool IsStar = false,
    Expression? Filter = null,
    OrderByClause? OrderWithin = null) : Expression;

public sealed record WindowSpecification(
    IReadOnlyList<Expression>? PartitionBy,
    OrderByClause? OrderBy,
    WindowFrame? Frame = null) : SqlNode;

/// <summary>Wunsch 4: <c>ROWS|RANGE [BETWEEN] start [AND end]</c>; offsets are non-negative integer literals only.</summary>
public sealed record WindowFrame(WindowFrameType Type, FrameBound Start, FrameBound? End) : SqlNode;

public sealed record FrameBound(FrameBoundKind Kind, long Offset = 0) : SqlNode;

public enum WindowFrameType
{
    Rows,
    Range
}

public enum FrameBoundKind
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing
}

/// <summary>Wunsch 4: <c>current_date</c>, <c>current_time</c>, <c>current_timestamp</c>, <c>localtime</c>, <c>localtimestamp</c>.</summary>
public sealed record CurrentDateTimeExpression(CurrentDateTimeKind Kind) : Expression;

public enum CurrentDateTimeKind
{
    CurrentDate,
    CurrentTime,
    CurrentTimestamp,
    LocalTime,
    LocalTimestamp
}

/// <summary>Wunsch 4: <c>substring(x FROM start [FOR length])</c>.</summary>
public sealed record SubstringExpression(Expression Source, Expression Start, Expression? Length) : Expression;

/// <summary>Wunsch 4: <c>trim([BOTH|LEADING|TRAILING] [chars] FROM x)</c> and <c>trim(x[, chars])</c>.</summary>
public sealed record TrimExpression(TrimSpecification Specification, Expression Source, Expression? Characters) : Expression;

public enum TrimSpecification
{
    Both,
    Leading,
    Trailing
}

/// <summary>Wunsch 4: <c>position(needle IN haystack)</c>, 1-based, 0 when not found.</summary>
public sealed record PositionExpression(Expression Needle, Expression Haystack) : Expression;

/// <summary>Wunsch 4: <c>GROUPING(col, …)</c>; with several columns a bitmask (SQL Server: GROUPING_ID).</summary>
public sealed record GroupingOperationExpression(IReadOnlyList<ColumnReference> Columns) : Expression;

/// <summary>Wunsch 4: <c>DATE '…'</c>, <c>TIME '…'</c>, <c>TIMESTAMP '…'</c>; the value is validated by the builder.</summary>
public sealed record TypedLiteralExpression(TypedLiteralKind Kind, string Value) : Expression;

public enum TypedLiteralKind
{
    Date,
    Time,
    Timestamp
}

/// <summary>
/// Virtual filters (phase 7b): Trino <c>date_add(unit, Amount, Source)</c> (also <c>Source ± INTERVAL</c>) and
/// <c>date_trunc(unit, Source)</c>; only built with <c>TranslateTrinoDateFunctions</c>. <c>Amount</c> is 0 for Trunc.
/// </summary>
public sealed record DateFunctionExpression(DateFunctionKind Kind, DateUnit Unit, long Amount, Expression Source) : Expression;

public enum DateFunctionKind
{
    Add,
    Trunc
}

public enum DateUnit
{
    Second,
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year
}

/// <summary>Wunsch 4: <c>INTERVAL '<Value>' <Field></c> with a single unsigned field.</summary>
public sealed record IntervalLiteralExpression(string Value, string Field) : Expression;

public sealed record CastExpression(
    Expression Operand,
    string TargetType,
    bool IsTryCast = false) : Expression;

public sealed record RowValueExpression(
    IReadOnlyList<Expression> Elements) : Expression;

public sealed record ArrayConstructorExpression(
    IReadOnlyList<Expression> Elements) : Expression;

public sealed record SubscriptExpression(
    Expression Target,
    Expression Index) : Expression;

public sealed record ExtractExpression(
    string Field,
    Expression Source) : Expression;
