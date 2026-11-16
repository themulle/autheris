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
        SqlBaseParser.BooleanExpressionContext? firstRight = null;
        List<SqlBaseParser.BooleanExpressionContext>? moreRights = null;
        int count = 0;
        SqlBaseParser.BooleanExpressionContext cur = context;
        while (cur is SqlBaseParser.AndContext and)
        {
            AddChainTerm(ref firstRight, ref moreRights, ref count, and.booleanExpression(1));
            if (count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Logical AND chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = and.booleanExpression(0);
        }

        var acc = (Expression)Visit(cur);
        for (int i = count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(i == 0 ? firstRight! : moreRights![i - 1]);
            acc = new BinaryExpression(acc, BinaryOperator.And, right);
        }

        return acc;
    }

    private Expression VisitOrChain(SqlBaseParser.OrContext context)
    {
        using var _ = EnterScope();
        SqlBaseParser.BooleanExpressionContext? firstRight = null;
        List<SqlBaseParser.BooleanExpressionContext>? moreRights = null;
        int count = 0;
        SqlBaseParser.BooleanExpressionContext cur = context;
        while (cur is SqlBaseParser.OrContext or)
        {
            AddChainTerm(ref firstRight, ref moreRights, ref count, or.booleanExpression(1));
            if (count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Logical OR chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = or.booleanExpression(0);
        }

        var acc = (Expression)Visit(cur);
        for (int i = count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(i == 0 ? firstRight! : moreRights![i - 1]);
            acc = new BinaryExpression(acc, BinaryOperator.Or, right);
        }

        return acc;
    }

    private Expression VisitConcatenationChain(SqlBaseParser.ConcatenationContext context)
    {
        using var _ = EnterScope();
        SqlBaseParser.ValueExpressionContext? firstRight = null;
        List<SqlBaseParser.ValueExpressionContext>? moreRights = null;
        int count = 0;
        SqlBaseParser.ValueExpressionContext cur = context;
        while (cur is SqlBaseParser.ConcatenationContext concat)
        {
            AddChainTerm(ref firstRight, ref moreRights, ref count, concat.right);
            if (count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Concatenation chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = concat.left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = count - 1; i >= 0; i--)
        {
            var right = (Expression)Visit(i == 0 ? firstRight! : moreRights![i - 1]);
            acc = new BinaryExpression(acc, BinaryOperator.Concat, right);
        }

        return acc;
    }

    private Expression VisitArithmeticChain(SqlBaseParser.ArithmeticBinaryContext context)
    {
        using var _ = EnterScope();
        (SqlBaseParser.ValueExpressionContext Right, BinaryOperator Op) firstRight = default;
        List<(SqlBaseParser.ValueExpressionContext Right, BinaryOperator Op)>? moreRights = null;
        int count = 0;
        SqlBaseParser.ValueExpressionContext cur = context;
        while (cur is SqlBaseParser.ArithmeticBinaryContext arith)
        {
            var op = MapArithmeticOperator(arith.@operator);
            AddChainTerm(ref firstRight, ref moreRights, ref count, (arith.right, op));
            if (count >= MaxOperatorChainTerms)
            {
                throw new AstBuildException(
                    $"Arithmetic chain exceeded the maximum allowed length of {MaxOperatorChainTerms} terms.");
            }

            cur = arith.left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = count - 1; i >= 0; i--)
        {
            var (rightCtx, op) = i == 0 ? firstRight : moreRights![i - 1];
            var right = (Expression)Visit(rightCtx);
            acc = _options.TranslateTrinoDateFunctions && (acc is IntervalLiteralExpression || right is IntervalLiteralExpression)
                ? BuildIntervalArithmetic(acc, op, right)
                : new BinaryExpression(acc, op, right);
        }

        return acc;
    }

    // The first term lives in a local and the list is created only for a second one: most chains have a single term, and the
    // per-chain list and its backing array were a measurable share of the allocations of a small statement.
    private static void AddChainTerm<T>(ref T? first, ref List<T>? more, ref int count, T term)
    {
        if (count == 0)
        {
            first = term;
        }
        else
        {
            (more ??= []).Add(term);
        }

        count++;
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
