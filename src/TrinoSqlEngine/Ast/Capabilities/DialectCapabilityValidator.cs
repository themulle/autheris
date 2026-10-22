namespace TrinoSqlEngine.Ast.Capabilities;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Validates a secured AST against the capability table before emission: constructs the dialect cannot express, IN-list size
/// and identifier length. Over-limit input is rejected with a typed error, never split, truncated or compacted (AP-11, B-3).
/// </summary>
public static class DialectCapabilityValidator
{
    public static void Validate(SqlStatement statement, DialectCapabilities capabilities)
    {
        if (capabilities.MaxExpressionDepth is { } maxDepth)
        {
            CheckExpressionDepth(statement, capabilities.Dialect, maxDepth);
        }

        AstReflection.Walk(statement, node =>
        {
            switch (node)
            {
                case LateralTableSource when !capabilities.SupportsLateral:
                    throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "LATERAL");
                case MergeStatement merge when capabilities.MergeShape == MergeClauseShape.OracleSingleClause:
                    ValidateSingleClauseMerge(merge);
                    break;
                case SetOperationQuery { Right: SetOperationQuery right } outer when !capabilities.IntersectBindsTighter
                    && !IsAssociativeUnionPair(outer, right):
                    // SQLE-03: a right-nested set operation arises only from INTERSECT precedence (parenthesized primaries are rejected by the
                    // builder); the emitter prints the operands flat, so a dialect that evaluates left to right would change the result.
                    // Exception: UNION of the same kind is associative, so the balanced INSERT check-option row tree (CR-ADG-44) stays valid.
                    throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "INTERSECT mixed with UNION/EXCEPT");
                case SetOperationQuery setOperation when capabilities.MaxCompoundSelectTerms is { } maxTerms:
                    int terms = CountTerms(setOperation);
                    if (terms > maxTerms)
                    {
                        throw new SqlLimitExceededException(SqlLimitKind.CompoundSelectTerms, capabilities.Dialect, terms, maxTerms);
                    }

                    break;
                case LikeExpression lk when capabilities.LikeIsCaseInsensitive && LikePatternEscaper.ContainsPolicyParameter(lk.Pattern):
                    // SEC-ADG-18: the dialect LIKE is looser than the policy author expects (SQLite: ASCII case-insensitive).
                    throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "policy LIKE on a case-insensitive LIKE dialect");
                case InListExpression inList when capabilities.MaxInListItems is { } max && inList.Items.Count > max
                                                    && !CompilerHooks.IsDisabled(SecurityControl.InListLimit)
                                                    :
                    throw new SqlLimitExceededException(SqlLimitKind.InListItems, capabilities.Dialect, inList.Items.Count, max);
                case SqlIdentifier id:
                    int length = capabilities.IdentifierLengthUnit == IdentifierLengthUnit.Bytes
                        ? Encoding.UTF8.GetByteCount(id.Value)
                        : id.Value.Length;
                    if (length > capabilities.MaxIdentifierLength)
                    {
                        throw new SqlLimitExceededException(SqlLimitKind.IdentifierLength, capabilities.Dialect, length, capabilities.MaxIdentifierLength);
                    }

                    break;
            }

            return true;
        });
    }

    /// <summary>The leaves of one connected UNION/INTERSECT/EXCEPT tree (SQLite counts them against SQLITE_MAX_COMPOUND_SELECT).</summary>
    private static bool IsAssociativeUnionPair(SetOperationQuery outer, SetOperationQuery right) =>
        outer.Operator == SetOperator.Union && right.Operator == SetOperator.Union && outer.Distinct == right.Distinct;

    private static int CountTerms(QueryBody body)
    {
        int count = 0;
        var pending = new Stack<QueryBody>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            if (pending.Pop() is SetOperationQuery set)
            {
                pending.Push(set.Left);
                pending.Push(set.Right);
            }
            else
            {
                count++;
            }
        }

        return count;
    }

    private static void CheckExpressionDepth(SqlStatement statement, TargetSqlDialect dialect, int maxDepth)
    {
        try
        {
            var deepestSeen = new DepthHolder();
            DeepestExpression(statement, 0, deepestSeen);
            int deepest = deepestSeen.Value;
            if (deepest > maxDepth)
            {
                throw new SqlLimitExceededException(SqlLimitKind.ExpressionDepth, dialect, deepest, maxDepth);
            }
        }
        catch (InsufficientExecutionStackException)
        {
            throw new SqlLimitExceededException(SqlLimitKind.ExpressionDepth, dialect, maxDepth + 1L, maxDepth);
        }
    }

    private sealed class DepthHolder
    {
        public int Value { get; set; }
    }

    private static void DeepestExpression(object? node, int depth, DepthHolder seen)
    {
        if (node is null or string) return;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is Expression) depth++;
        if (depth > seen.Value) seen.Value = depth;
        AstReflection.Children(node, (depth, seen), static (child, state) => DeepestExpression(child, state.depth, state.seen));
    }

    private static void ValidateSingleClauseMerge(MergeStatement merge)
    {
        int updates = 0, inserts = 0;
        foreach (var clause in merge.Clauses)
        {
            switch (clause)
            {
                case MergeDeleteClause:
                    throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "MERGE WHEN MATCHED THEN DELETE");
                case MergeUpdateClause:
                    updates++;
                    break;
                case MergeInsertClause:
                    inserts++;
                    break;
            }
        }

        if (updates > 1 || inserts > 1)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "MERGE with repeated WHEN clause kinds");
        }
    }
}
