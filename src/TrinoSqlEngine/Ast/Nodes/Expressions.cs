namespace TrinoSqlEngine.Ast.Nodes;

using System;
using System.Collections.Generic;

public abstract record Expression : SqlNode;

public sealed record ColumnReference(SqlQualifiedName Name) : Expression;

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

public sealed record FunctionCallExpression(
    SqlQualifiedName Name,
    IReadOnlyList<Expression> Arguments,
    bool Distinct = false,
    WindowSpecification? Window = null) : Expression;

public sealed record WindowSpecification(
    IReadOnlyList<Expression>? PartitionBy,
    OrderByClause? OrderBy) : SqlNode;

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
