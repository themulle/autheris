namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// CR-ADG-42: how the row policy is evaluated over the inserted values. The values are already cast to the stored column types
/// (see <see cref="CatalogTypeMap"/>), but a string comparison would still use the collation of the database, which can differ from
/// the collation of the column the row is stored in (and from the collation of the reader's filter). So a string column of the
/// check may only appear in an equality or IN predicate with a bound value, and that predicate is evaluated byte-exact with the
/// tenant comparison machinery: a byte-exact match is never looser than any collation. A negated predicate (NOT, <c>&lt;&gt;</c>,
/// NOT IN), a range, LIKE, BETWEEN or any function or expression over a string column is rejected with a typed error: byte-exact
/// inequality is wider than a case-insensitive one. The injector rewrites with <see cref="Rewrite"/>; the coverage verifier proves
/// the result with <see cref="Validate"/> and never trusts the injector.
/// </summary>
internal static class InsertCheckPolicy
{
    internal sealed class UnsupportedStringPredicateException(string message) : Exception(message);

    public static Expression Rewrite(Expression policy, SqlIdentifier alias, Func<string, bool> isTextColumn, DialectCapabilities capabilities) =>
        new Walker(alias, isTextColumn, capabilities, rewrite: true).Run(policy);

    /// <summary>Throws <see cref="UnsupportedStringPredicateException"/> when a string column is not compared byte-exact (or is used otherwise).</summary>
    public static void Validate(Expression policy, SqlIdentifier alias, Func<string, bool> isTextColumn, DialectCapabilities capabilities) =>
        new Walker(alias, isTextColumn, capabilities, rewrite: false).Run(policy);

    private sealed class Walker(SqlIdentifier alias, Func<string, bool> isTextColumn, DialectCapabilities capabilities, bool rewrite)
    {
        public Expression Run(Expression policy) => Visit(policy, negated: false);

        private bool IsTextColumn(Expression? expression) =>
            expression is ColumnReference { Name.Parts: { Count: 2 } parts } &&
            string.Equals(parts[0].Value, alias.Value, StringComparison.Ordinal) && isTextColumn(parts[1].Value);

        private static bool IsValue(Expression expression) => expression is PolicyParameterExpression or LiteralExpression;

        /// <summary>Does <paramref name="node"/> read a string column of the inserted values (nested SELECTs belong to other tables and are skipped)?</summary>
        private bool TouchesText(object? node)
        {
            bool found = false;
            AstReflection.Walk(node, n =>
            {
                if (n is SelectStatement) return false;
                if (IsTextColumn(n as Expression)) found = true;
                return !found;
            });
            return found;
        }

        private static UnsupportedStringPredicateException Reject() =>
            new("An INSERT check on a string column supports only equality and IN with a bound value.");

        private Expression Visit(Expression e, bool negated)
        {
            switch (e)
            {
                case ParenthesizedExpression p:
                {
                    var inner = Visit(p.Expression, negated);
                    return ReferenceEquals(inner, p.Expression) ? p : p with { Expression = inner };
                }
                case BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or } b:
                {
                    if (!rewrite && b.Operator == BinaryOperator.And && TouchesText(b) && TryExactEquals(b, out var expected) && AstReflection.StructurallyEqual(b, expected))
                    {
                        return negated ? throw Reject() : b;
                    }

                    var left = Visit(b.Left, negated);
                    var right = Visit(b.Right, negated);
                    return ReferenceEquals(left, b.Left) && ReferenceEquals(right, b.Right) ? b : b with { Left = left, Right = right };
                }
                case UnaryExpression { Operator: UnaryOperator.Not } n:
                {
                    var inner = Visit(n.Operand, negated: true);
                    return ReferenceEquals(inner, n.Operand) ? n : n with { Operand = inner };
                }
                case UnaryExpression { Operator: UnaryOperator.IsNull or UnaryOperator.IsNotNull } nullTest when nullTest.Operand is ColumnReference:
                    return nullTest;
                case BinaryExpression { Operator: BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                    or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual } comparison when TouchesText(comparison):
                {
                    if (negated) throw Reject();
                    if (!rewrite)
                    {
                        // a bare comparison that touches a string column is exactly what the verifier must not accept
                        throw Reject();
                    }

                    if (comparison.Operator != BinaryOperator.Equal) throw Reject();
                    bool left = IsTextColumn(comparison.Left), right = IsTextColumn(comparison.Right);
                    if (left == right) throw Reject();
                    var column = left ? comparison.Left : comparison.Right;
                    var value = left ? comparison.Right : comparison.Left;
                    if (!IsValue(value)) throw Reject();
                    return new ParenthesizedExpression(TenantPredicateFactory.ExactEquals(capabilities, column, value));
                }
                case InListExpression list when TouchesText(list):
                {
                    if (negated || !rewrite || list.IsNotIn || !IsTextColumn(list.Operand) || list.Items.Count == 0) throw Reject();
                    Expression? result = null;
                    foreach (var item in list.Items)
                    {
                        if (!IsValue(item)) throw Reject();
                        var equality = new ParenthesizedExpression(TenantPredicateFactory.ExactEquals(capabilities, list.Operand, item));
                        result = result is null ? equality : new BinaryExpression(result, BinaryOperator.Or, equality);
                    }

                    return new ParenthesizedExpression(result!);
                }
                default:
                    if (TouchesText(e)) throw Reject();
                    return e;
            }
        }

        /// <summary>The leftmost <c>column = value</c> of an equality chain, rebuilt through the factory.</summary>
        private bool TryExactEquals(BinaryExpression chain, out Expression expected)
        {
            expected = chain;
            Expression current = chain;
            while (current is BinaryExpression { Operator: BinaryOperator.And } and) current = and.Left;
            if (current is not BinaryExpression { Operator: BinaryOperator.Equal } leaf) return false;
            bool left = IsTextColumn(leaf.Left), right = IsTextColumn(leaf.Right);
            if (left == right) return false;
            var column = left ? leaf.Left : leaf.Right;
            var value = left ? leaf.Right : leaf.Left;
            if (!IsValue(value)) return false;
            expected = TenantPredicateFactory.ExactEquals(capabilities, column, value);
            return true;
        }
    }
}
