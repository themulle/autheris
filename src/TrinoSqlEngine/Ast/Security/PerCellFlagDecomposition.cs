namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// Decomposes a table's <see cref="PerCellTablePolicy"/> into deduplicated <c>LEFT JOIN (SELECT DISTINCT ...)</c>
/// flag subqueries for correlated <c>EXISTS</c> scopes, boolean flag expressions per signature, and column branch conditions.
/// Shared by the security injector and coverage verifier so both derive identical flag shapes from the provider.
/// </summary>
internal sealed class PerCellFlagDecomposition
{
    internal const string FlagAliasPrefix = "__f_";
    internal const string FlagKeyPrefix = "__autheris_fk_";

    internal sealed record FlagJoin(
        SqlIdentifier Alias,
        SelectStatement Subquery,
        OnJoinCondition OnCondition,
        ImmutableArray<string> TargetColumns);

    internal sealed record ColumnBranches(
        Expression? ClearCondition,
        Expression? MaskCondition,
        string? ClearConditionFingerprint,
        string? MaskConditionFingerprint);

    internal sealed record Result(
        IReadOnlyList<FlagJoin> FlagJoins,
        IReadOnlyList<Expression> SignatureFlags,
        IReadOnlyDictionary<string, ColumnBranches> Columns,
        Expression? GrantUnionPredicate,
        bool ReferencesTarget);

    public static Result Decompose(
        PerCellTablePolicy policy,
        TableCatalogEntry entry,
        Func<Expression, Expression>? rewriteExpression = null,
        Func<SelectStatement, SelectStatement>? rewriteSubquery = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(entry);

        var joins = new List<FlagJoin>();
        var joinKeyToAlias = new Dictionary<string, SqlIdentifier>(StringComparer.Ordinal);
        var signatureFlags = new List<Expression>(policy.Signatures.Count);

        for (int i = 0; i < policy.Signatures.Count; i++)
        {
            var sig = policy.Signatures[i];
            if (sig.IsUnfiltered)
            {
                signatureFlags.Add(Tautology());
                continue;
            }

            var rewriter = new ExistsToFlagRewriter(joins, joinKeyToAlias, entry, rewriteSubquery);
            var flag = rewriter.Rewrite(sig.Scope.Expression);
            if (rewriteExpression != null)
            {
                flag = rewriteExpression(flag);
            }

            flag = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(flag);
            signatureFlags.Add(flag);
        }

        var columns = new Dictionary<string, ColumnBranches>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colName, rule) in policy.Columns)
        {
            var clearCond = OrChain(rule.ClearSignatures, signatureFlags);
            var maskCond = OrChain(rule.MaskedSignatures, signatureFlags);
            columns[colName] = new ColumnBranches(
                clearCond,
                maskCond,
                clearCond is null ? null : AstReflection.Fingerprint(clearCond),
                maskCond is null ? null : AstReflection.Fingerprint(maskCond));
        }

        var allIndices = Enumerable.Range(0, signatureFlags.Count).ToImmutableArray();
        var grantUnion = policy.AnySignatureUnfiltered ? null : OrChain(allIndices, signatureFlags);

        bool referencesTarget = joins.Count > 0 ||
            signatureFlags.Any(ReferencesTargetAlias) ||
            (policy.NarrowingPredicate is not null && ReferencesTargetAlias(policy.NarrowingPredicate.Expression));

        return new Result(joins, signatureFlags, columns, grantUnion, referencesTarget);
    }

    internal static bool ReferencesTargetAlias(Expression expression) =>
        AstReflection.Collect<ColumnReference>(expression).Any(IsTargetReference);

    private static bool IsTargetReference(ColumnReference col) =>
        col.Name.Parts.Count >= 2 &&
        string.Equals(col.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase);

    private static BinaryExpression Tautology() =>
        new(new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer));

    private static Expression? OrChain(ImmutableArray<int> indices, IReadOnlyList<Expression> flags)
    {
        if (indices.IsDefaultOrEmpty)
        {
            return null;
        }

        Expression? combined = null;
        foreach (int idx in indices)
        {
            var expr = flags[idx];
            combined = combined is null ? expr : new BinaryExpression(combined, BinaryOperator.Or, expr);
        }

        return combined;
    }

    private sealed class ExistsToFlagRewriter(
        List<FlagJoin> joins,
        Dictionary<string, SqlIdentifier> joinKeyToAlias,
        TableCatalogEntry targetEntry,
        Func<SelectStatement, SelectStatement>? rewriteSubquery)
    {
        public Expression Rewrite(Expression node) => node switch
        {
            ExistsExpression ex => RewriteExists(ex),
            ParenthesizedExpression p => p with { Expression = Rewrite(p.Expression) },
            UnaryExpression u => u with { Operand = Rewrite(u.Operand) },
            BinaryExpression b => b with { Left = Rewrite(b.Left), Right = Rewrite(b.Right) },
            _ => node
        };

        private Expression RewriteExists(ExistsExpression node)
        {
            if (TryDecomposeExists(node.Subquery, out var subquery, out var targetCols))
            {
                string dedupKey = string.Concat(
                    AstReflection.Fingerprint(subquery),
                    "|",
                    string.Join(',', targetCols));

                if (!joinKeyToAlias.TryGetValue(dedupKey, out var flagAlias))
                {
                    flagAlias = new SqlIdentifier($"{FlagAliasPrefix}{joins.Count}", IsQuoted: true);
                    joinKeyToAlias[dedupKey] = flagAlias;

                    var onPredicates = new List<Expression>(targetCols.Length);
                    for (int p = 0; p < targetCols.Length; p++)
                    {
                        var leftCol = new ColumnReference(new SqlQualifiedName([
                            flagAlias,
                            new SqlIdentifier($"{FlagKeyPrefix}{p}", IsQuoted: true)
                        ]));
                        var rightCol = new ColumnReference(new SqlQualifiedName([
                            new SqlIdentifier(RowFilterAliases.Target),
                            new SqlIdentifier(targetCols[p], IsQuoted: true)
                        ]));
                        onPredicates.Add(new BinaryExpression(leftCol, BinaryOperator.Equal, rightCol));
                    }

                    var onCondition = new OnJoinCondition(AndChain(onPredicates)!);
                    var rewrittenSubquery = rewriteSubquery != null ? rewriteSubquery(subquery) : subquery;
                    joins.Add(new FlagJoin(flagAlias, rewrittenSubquery, onCondition, targetCols));
                }

                var keyProbe = new ColumnReference(new SqlQualifiedName([
                    flagAlias,
                    new SqlIdentifier($"{FlagKeyPrefix}0", IsQuoted: true)
                ]));
                return new UnaryExpression(UnaryOperator.IsNotNull, keyProbe);
            }

            return node;
        }

        private bool TryDecomposeExists(
            SelectStatement subquery,
            out SelectStatement distinctSubquery,
            out ImmutableArray<string> targetColumns)
        {
            distinctSubquery = null!;
            targetColumns = ImmutableArray<string>.Empty;

            if (subquery.With is not null || subquery.OrderBy is not null || subquery.Pagination is not null)
            {
                return false;
            }

            if (subquery.Body is not QuerySpecification spec ||
                spec.GroupBy is not null || spec.Having is not null ||
                spec.From is null || spec.Where is null)
            {
                return false;
            }

            if (StripParentheses(spec.Where) is BinaryExpression { Operator: BinaryOperator.Or })
            {
                return false;
            }

            var conjuncts = FlattenAnd(spec.Where);
            var keyPairs = new List<(ColumnReference DepCol, string TargetCol)>();
            var remaining = new List<Expression>(conjuncts.Count);

            foreach (var conjunct in conjuncts)
            {
                if (StripParentheses(conjunct) is BinaryExpression { Operator: BinaryOperator.Equal, Left: ColumnReference left, Right: ColumnReference right })
                {
                    bool leftTarget = IsTargetReference(left);
                    bool rightTarget = IsTargetReference(right);
                    if (leftTarget != rightTarget)
                    {
                        var (depCol, targetCol) = leftTarget ? (right, left) : (left, right);
                        if (targetCol.Name.Parts.Count == 2 && depCol.Name.Parts.Count is 1 or 2)
                        {
                            string targetColName = targetCol.Name.Parts[^1].Value;
                            var resolved = targetEntry.Columns.FirstOrDefault(c =>
                                string.Equals(c.Name, targetColName, StringComparison.OrdinalIgnoreCase));
                            if (resolved is null)
                            {
                                return false;
                            }

                            keyPairs.Add((depCol, resolved.Name));
                            continue;
                        }

                        return false;
                    }
                }

                remaining.Add(conjunct);
            }

            if (keyPairs.Count == 0)
            {
                return false;
            }

            if (remaining.Any(ReferencesTargetAlias) ||
                AstReflection.Collect<ColumnReference>(spec.From).Any(IsTargetReference) ||
                HasTargetAlias(spec.From))
            {
                return false;
            }

            var keyProjections = new List<SelectItem>(keyPairs.Count);
            var nonNullGuards = new List<Expression>(remaining.Count + keyPairs.Count);
            nonNullGuards.AddRange(remaining);

            var targetColNames = ImmutableArray.CreateBuilder<string>(keyPairs.Count);
            for (int p = 0; p < keyPairs.Count; p++)
            {
                var (depCol, targetColName) = keyPairs[p];
                keyProjections.Add(new ColumnSelectItem(depCol, new SqlIdentifier($"{FlagKeyPrefix}{p}", IsQuoted: true)));
                nonNullGuards.Add(new UnaryExpression(UnaryOperator.IsNotNull, depCol));
                targetColNames.Add(targetColName);
            }

            var innerSpec = new QuerySpecification(
                Distinct: true,
                keyProjections,
                spec.From,
                AndChain(nonNullGuards),
                GroupBy: null,
                Having: null);
            distinctSubquery = new SelectStatement(null, innerSpec, null, null);
            targetColumns = targetColNames.ToImmutable();
            return true;
        }
    }

    private static Expression StripParentheses(Expression expression)
    {
        while (expression is ParenthesizedExpression parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static List<Expression> FlattenAnd(Expression where)
    {
        var result = new List<Expression>();
        var stack = new Stack<Expression>();
        stack.Push(where);
        while (stack.Count > 0)
        {
            var current = StripParentheses(stack.Pop());
            if (current is BinaryExpression { Operator: BinaryOperator.And } b)
            {
                stack.Push(b.Right);
                stack.Push(b.Left);
            }
            else
            {
                result.Add(current);
            }
        }

        return result;
    }

    private static Expression? AndChain(IReadOnlyList<Expression> conjuncts)
    {
        Expression? combined = null;
        foreach (var conjunct in conjuncts)
        {
            combined = combined is null ? conjunct : new BinaryExpression(combined, BinaryOperator.And, conjunct);
        }

        return combined;
    }

    private static bool HasTargetAlias(TableSource source) => source switch
    {
        NamedTableSource n => n.Alias is { } a && string.Equals(a.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase),
        SubqueryTableSource s => string.Equals(s.Alias.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) || HasTargetAlias(s.Subquery),
        LateralTableSource l => string.Equals(l.Alias.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) || HasTargetAlias(l.Subquery),
        JoinedTableSource j => HasTargetAlias(j.Left) || HasTargetAlias(j.Right),
        _ => false
    };

    private static bool HasTargetAlias(SelectStatement select)
    {
        if (select.With is not null && select.With.Ctes.Any(c => HasTargetAlias(c.Query)))
        {
            return true;
        }

        return HasTargetAlias(select.Body);
    }

    private static bool HasTargetAlias(QueryBody body) => body switch
    {
        QuerySpecification spec => spec.From is not null && HasTargetAlias(spec.From),
        SetOperationQuery set => HasTargetAlias(set.Left) || HasTargetAlias(set.Right),
        _ => false
    };
}
