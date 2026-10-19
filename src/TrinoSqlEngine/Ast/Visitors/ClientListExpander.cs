namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// REQ-SQL-128 pass <c>"lists"</c>. Replaces an <c>IN</c> / <c>NOT IN</c> list whose only item is a synthetic client parameter named in
/// the request's <see cref="ClientListShapes"/> with one indexed <see cref="ParameterReference"/> per element (an empty list becomes
/// the constant <c>1 = 0</c> / <c>1 = 1</c>). Every other occurrence of such a parameter is refused (SQL_LIST_PARAM_POSITION).
/// It runs on the USER tree only: after simplification and before the typed security visitor (SEC-128-01), so the DML guard sees the
/// expanded predicate. No element value is known here; elements are bound by the binder from the request.
/// </summary>
internal sealed class ClientListExpander : SqlAstRewriter
{
    /// <summary>Upper bound of the text one expanded element adds: the longest marker plus the separator (PLAN-SQL-128 D-EXP).</summary>
    public const int MarkerAllowance = 12;

    /// <summary>Hard ceiling of expanded items per statement, independent of the dialect budget (SEC-128-02).</summary>
    public const int ExpandedItemsCeiling = 10_000;

    private static readonly BinaryExpression ConstantFalse = new(
        new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(0L, LiteralType.Integer));

    private static readonly BinaryExpression ConstantTrue = new(
        new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer));

    private readonly ClientListShapes _shapes;
    private readonly int _maxExpanded;

    public ClientListExpander(ClientListShapes shapes, DialectCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(capabilities);
        _shapes = shapes;
        _maxExpanded = MaxExpandedItems(capabilities);
    }

    /// <summary>Running total of expanded items over all uses; the output allowance of the compiler is derived from it (SEC-128-03).</summary>
    public long ExpandedItems { get; private set; }

    /// <summary><c>min(MaxBindParameters - 100, 10,000)</c> (SEC-128-02).</summary>
    public static int MaxExpandedItems(DialectCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return Math.Max(0, Math.Min(capabilities.MaxBindParameters - 100, ExpandedItemsCeiling));
    }

    public override SqlNode VisitInListExpression(InListExpression node)
    {
        // The sole-item rule is on the exact node type: a parenthesized or wrapped item is never unwrapped (SEC-128-10 d).
        if (node.Items.Count == 1
            && node.Items[0] is ParameterReference { IsSynthetic: true, ListIndex: null } reference
            && _shapes.TryGetCount(reference.Name, out int count))
        {
            var operand = (Expression)Visit(node.Operand);
            if (count == 0)
            {
                return node.IsNotIn ? ConstantTrue : ConstantFalse;
            }

            // Checked before any node is allocated (SEC-128-02).
            if (!CompilerHooks.IsDisabled(SecurityControl.ClientListMaxExpanded) && ExpandedItems + count > _maxExpanded)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
            }

            ExpandedItems += count;
            var items = new Expression[count];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new ParameterReference(reference.Name, null, true, i, count);
            }

            return new InListExpression(operand, items, node.IsNotIn);
        }

        return base.VisitInListExpression(node);
    }

    public override SqlNode VisitParameterReference(ParameterReference node)
    {
        // Residual position check: any other synthetic reference to a list name (=, function argument, LIMIT, CASE, VALUES, select list,
        // left side of IN, mixed items) is refused. A reference that already carries an index was produced by this pass.
        if (node.IsSynthetic && node.ListIndex is null && _shapes.TryGetCount(node.Name, out _)
            && !CompilerHooks.IsDisabled(SecurityControl.ClientListPosition))
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamPosition);
        }

        return node;
    }
}
