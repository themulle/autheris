namespace TrinoSqlEngine.Ast.Builder;

using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class SqlAstBuilder
{
    /// <summary>
    /// Maximum number of terms permitted in a single contiguous flat left spine of
    /// <c>AND</c>, <c>OR</c>, <c>||</c>, or arithmetic operators.
    /// </summary>
    public const int MaxOperatorChainTerms = 1_000;

    internal static bool CanUnrollLeftSpine(BinaryOperator op) =>
        op is BinaryOperator.And
            or BinaryOperator.Or
            or BinaryOperator.Concat
            or BinaryOperator.Add
            or BinaryOperator.Subtract
            or BinaryOperator.Multiply
            or BinaryOperator.Divide
            or BinaryOperator.Modulo;

    internal static bool SameChainFamily(BinaryOperator outer, BinaryOperator inner) =>
        (outer == inner && outer is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Concat)
        || (IsArithmeticOperator(outer) && IsArithmeticOperator(inner));

    private static bool IsArithmeticOperator(BinaryOperator op) =>
        op is BinaryOperator.Add
            or BinaryOperator.Subtract
            or BinaryOperator.Multiply
            or BinaryOperator.Divide
            or BinaryOperator.Modulo;

    private Expression VisitAndChain(SqlBaseParser.AndContext context)
    {
        using var _ = EnterScope();
        var rights = new List<SqlBaseParser.BooleanExpressionContext>();
        SqlBaseParser.BooleanExpressionContext cur = context;
        while (cur is SqlBaseParser.AndContext and)
        {
            rights.Add(and.booleanExpression(1));
            if (rights.Count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Logical AND chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = and.booleanExpression(0);
        }

        var acc = (Expression)Visit(cur);
        for (int i = rights.Count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(rights[i]);
            acc = new BinaryExpression(acc, BinaryOperator.And, right);
        }

        return acc;
    }

    private Expression VisitOrChain(SqlBaseParser.OrContext context)
    {
        using var _ = EnterScope();
        var rights = new List<SqlBaseParser.BooleanExpressionContext>();
        SqlBaseParser.BooleanExpressionContext cur = context;
        while (cur is SqlBaseParser.OrContext or)
        {
            rights.Add(or.booleanExpression(1));
            if (rights.Count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Logical OR chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = or.booleanExpression(0);
        }

        var acc = (Expression)Visit(cur);
        for (int i = rights.Count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(rights[i]);
            acc = new BinaryExpression(acc, BinaryOperator.Or, right);
        }

        return acc;
    }

    private Expression VisitConcatenationChain(SqlBaseParser.ConcatenationContext context)
    {
        using var _ = EnterScope();
        var rights = new List<SqlBaseParser.ValueExpressionContext>();
        SqlBaseParser.ValueExpressionContext cur = context;
        while (cur is SqlBaseParser.ConcatenationContext concat)
        {
            rights.Add(concat.right);
            if (rights.Count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Concatenation chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = concat.left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = rights.Count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(rights[i]);
            acc = new BinaryExpression(acc, BinaryOperator.Concat, right);
        }

        return acc;
    }

    private Expression VisitArithmeticChain(SqlBaseParser.ArithmeticBinaryContext context)
    {
        using var _ = EnterScope();
        var rights = new List<(SqlBaseParser.ValueExpressionContext Right, BinaryOperator Op)>();
        SqlBaseParser.ValueExpressionContext cur = context;
        while (cur is SqlBaseParser.ArithmeticBinaryContext arith)
        {
            var op = MapArithmeticOperator(arith.@operator);
            rights.Add((arith.right, op));
            if (rights.Count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Arithmetic chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = arith.left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = rights.Count - 1; i >= 0; i--)
        {
            var (rightCtx, op) = rights[i];
            var right = (Expression)Visit(rightCtx);
            acc = _options.TranslateTrinoDateFunctions && (acc is IntervalLiteralExpression || right is IntervalLiteralExpression)
                ? BuildIntervalArithmetic(acc, op, right)
                : new BinaryExpression(acc, op, right);
        }

        return acc;
    }

    private static BinaryOperator MapArithmeticOperator(IToken opToken) => opToken.Type switch
    {
        SqlBaseLexer.PLUS => BinaryOperator.Add,
        SqlBaseLexer.MINUS => BinaryOperator.Subtract,
        SqlBaseLexer.ASTERISK => BinaryOperator.Multiply,
        SqlBaseLexer.SLASH => BinaryOperator.Divide,
        SqlBaseLexer.PERCENT => BinaryOperator.Modulo,
        _ => throw new AstBuildException($"Unknown binary operator: {opToken.Text}")
    };
}
