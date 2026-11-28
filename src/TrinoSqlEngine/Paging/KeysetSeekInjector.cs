namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Builds the seek predicate (rows strictly after the last delivered row in ORDER BY order) in the expanded OR form that every
/// supported dialect evaluates the same way, and ANDs it with the user's WHERE. The values are bound slots, never text.
/// </summary>
internal static class KeysetSeekInjector
{
    public static SqlStatement Inject(
        SqlStatement userTree,
        KeysetShape shape,
        ImmutableArray<SqlParameterType> types,
        IReadOnlyList<string?> columnTypes)
    {
        if (userTree is not SelectStatement { Body: QuerySpecification spec } select || shape.Table is null
            || shape.Columns.Length != types.Length || shape.Columns.Length != columnTypes.Count)
        {
            throw new KeysetSeekRejectedException();
        }

        var qualifier = KeysetShapeAnalyzer.QualifierOf(shape.Table);
        Expression Column(int i) => new ColumnReference(new SqlQualifiedName(new[] { qualifier, shape.ColumnIdentifiers[i] }));
        Expression Slot(int i) => new KeysetSeekParameterExpression(i, types[i], columnTypes[i]);

        Expression? seek = null;
        for (int i = 0; i < shape.Columns.Length; i++)
        {
            Expression? term = null;
            for (int j = 0; j < i; j++)
            {
                var equal = new BinaryExpression(Column(j), BinaryOperator.Equal, Slot(j));
                term = term is null ? equal : new BinaryExpression(term, BinaryOperator.And, equal);
            }

            var after = new BinaryExpression(Column(i), shape.Descending[i] ? BinaryOperator.LessThan : BinaryOperator.GreaterThan, Slot(i));
            term = term is null ? after : new BinaryExpression(term, BinaryOperator.And, after);
            Expression group = new ParenthesizedExpression(term);
            seek = seek is null ? group : new BinaryExpression(seek, BinaryOperator.Or, group);
        }

        Expression predicate = spec.Where is null
            ? new ParenthesizedExpression(seek!)
            : new BinaryExpression(new ParenthesizedExpression(spec.Where), BinaryOperator.And, new ParenthesizedExpression(seek!));
        return select with { Body = spec with { Where = predicate } };
    }

    /// <summary>True when <paramref name="expression"/> contains a seek slot (such a conjunct never satisfies a required-filter check).</summary>
    public static bool ContainsSeek(SqlNode expression)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(expression);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is KeysetSeekParameterExpression)
            {
                return true;
            }

            foreach (var child in node.EnumerateChildren())
            {
                stack.Push(child);
            }
        }

        return false;
    }
}
