namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Issue #107 WP-B: discovery and substitution of correlated filter sites (PLAN-GOV-107 A-10, SEC-RF107-06, -10). A site is substituted only
/// when it is reached from the predicate root through <c>AND</c>, <c>OR</c> and parentheses and its node instance occurs exactly once in
/// the tree; every other position keeps the <c>EXISTS</c> (<c>NOT (fk IN ...)</c> and <c>NOT EXISTS</c> differ for NULL foreign keys, and a
/// shared instance could sit in a positive and a negated position). The result is a new <see cref="PolicyPredicate"/> in which only the
/// chosen nodes and their ancestors on the AND/OR path are new instances; the original predicate, and with it the decision it came from, is not touched.
/// </summary>
internal static class CorrelatedKeySetSubstitution
{
    /// <summary>The sites of a predicate: the ones a stage may substitute and the registered ones it must leave as <c>EXISTS</c> (reason <c>Deny_Negated</c>).</summary>
    internal sealed record SiteScan(
        IReadOnlyList<(ExistsExpression Node, CorrelatedFilterSite Site)> Eligible,
        IReadOnlyList<(ExistsExpression Node, CorrelatedFilterSite Site)> Excluded);

    /// <summary>Positive-polarity registered sites of the predicate that occur once, in a stable pre-order.</summary>
    public static IReadOnlyList<(ExistsExpression Node, CorrelatedFilterSite Site)> FindSites(PolicyPredicate predicate) => Scan(predicate).Eligible;

    public static SiteScan Scan(PolicyPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        // Positive polarity: only AND, OR and parentheses lead from the root to the site (pre-order, left before right).
        var positive = new List<ExistsExpression>();
        var pending = new Stack<Expression>();
        pending.Push(predicate.Expression);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case ParenthesizedExpression parenthesized:
                    pending.Push(parenthesized.Expression);
                    break;
                case BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or } binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;
                case ExistsExpression exists when CorrelatedFilterSiteRegistry.TryGet(exists, out _):
                    positive.Add(exists);
                    break;
            }
        }

        // Every occurrence anywhere in the tree: a registered instance that occurs more than once is not substituted anywhere.
        var total = new Dictionary<ExistsExpression, int>(ReferenceEqualityComparer.Instance);
        var order = new List<ExistsExpression>();
        AstReflection.Walk(predicate.Expression, node =>
        {
            if (node is ExistsExpression exists && CorrelatedFilterSiteRegistry.TryGet(exists, out _))
            {
                if (!total.TryGetValue(exists, out int seen)) order.Add(exists);
                total[exists] = seen + 1;
            }

            return true;
        });

        var positives = new Dictionary<ExistsExpression, int>(ReferenceEqualityComparer.Instance);
        foreach (var node in positive) positives[node] = positives.GetValueOrDefault(node) + 1;

        var eligible = new List<(ExistsExpression, CorrelatedFilterSite)>();
        var seenEligible = new HashSet<ExistsExpression>(ReferenceEqualityComparer.Instance);
        foreach (var node in positive)
        {
            if (total[node] == 1 && positives[node] == 1 && seenEligible.Add(node) && CorrelatedFilterSiteRegistry.TryGet(node, out var site))
            {
                eligible.Add((node, site));
            }
        }

        var excluded = new List<(ExistsExpression, CorrelatedFilterSite)>();
        foreach (var node in order)
        {
            if (!seenEligible.Contains(node) && CorrelatedFilterSiteRegistry.TryGet(node, out var site))
            {
                excluded.Add((node, site));
            }
        }

        return new SiteScan(eligible, excluded);
    }

    /// <summary>
    /// Replaces each given node (reference identity) by its IN list, or by <c>1 = 0</c> for an empty key set, and returns a new predicate.
    /// A node that is not an eligible site of <paramref name="predicate"/> (negated, shared, unregistered) is refused with an
    /// <see cref="InvalidOperationException"/>: the substitution never widens a filter by a caller's mistake (fail closed).
    /// D-WPC-6: the list form builds the reference-equality map itself, so two structurally equal <see cref="ExistsExpression"/> records
    /// can never be collapsed by a caller's default-comparer dictionary; the same node instance twice is an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static PolicyPredicate Substitute(
        PolicyPredicate predicate,
        IReadOnlyList<(ExistsExpression Node, KeySetReplacement Replacement)> replacements,
        TargetSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            return predicate;
        }

        var eligible = new HashSet<ExistsExpression>(ReferenceEqualityComparer.Instance);
        foreach (var (node, _) in Scan(predicate).Eligible) eligible.Add(node);

        var chosen = new Dictionary<ExistsExpression, KeySetReplacement>(ReferenceEqualityComparer.Instance);
        foreach (var (node, replacement) in replacements)
        {
            if (chosen.ContainsKey(node))
            {
                throw new InvalidOperationException("The same correlated filter site is replaced twice.");
            }

            if (!eligible.Contains(node))
            {
                throw new InvalidOperationException("A key-set replacement targets a node that is not a substitutable correlated filter site.");
            }

            if (!CorrelatedFilterSiteRegistry.TryGet(node, out var site) || !site.StructurallyEligible)
            {
                throw new InvalidOperationException("A key-set replacement targets a site that is not structurally eligible (temporal filter).");
            }

            chosen[node] = replacement;
        }

        var parameters = new Dictionary<string, PolicyValue>(predicate.Parameters, StringComparer.Ordinal);
        int? limit = PolicyParameterNamer.InListLimit(dialect);
        var expression = Rewrite(predicate.Expression, chosen, parameters, limit);
        return PolicyPredicate.Create(expression, parameters);
    }

    private static Expression Rewrite(
        Expression expression,
        Dictionary<ExistsExpression, KeySetReplacement> chosen,
        Dictionary<string, PolicyValue> parameters,
        int? limit)
    {
        ExecutionStackGuard.Ensure();
        switch (expression)
        {
            case ExistsExpression exists when chosen.TryGetValue(exists, out var replacement):
                return Leaf(replacement, parameters, limit);
            case ParenthesizedExpression parenthesized:
            {
                var inner = Rewrite(parenthesized.Expression, chosen, parameters, limit);
                return ReferenceEquals(inner, parenthesized.Expression) ? parenthesized : new ParenthesizedExpression(inner);
            }

            case BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or } binary:
            {
                var left = Rewrite(binary.Left, chosen, parameters, limit);
                var right = Rewrite(binary.Right, chosen, parameters, limit);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right) ? binary : binary with { Left = left, Right = right };
            }

            default:
                return expression;
        }
    }

    private static Expression Leaf(KeySetReplacement replacement, Dictionary<string, PolicyValue> parameters, int? limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.ForeignKeyColumn);
        if (replacement.Keys.Count == 0)
        {
            return new BinaryExpression(new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(0L, LiteralType.Integer));
        }

        if (replacement.PaddedCount < replacement.Keys.Count)
        {
            throw new ArgumentException("The padded key count is below the number of keys; keys are never truncated.", nameof(replacement));
        }

        var values = new List<PolicyValue>(replacement.PaddedCount);
        foreach (var key in replacement.Keys)
        {
            if (key.Value is null || key.Type == SqlParameterType.Null)
            {
                throw new ArgumentException("A key value must not be null.", nameof(replacement));
            }

            values.Add(key);
        }

        while (values.Count < replacement.PaddedCount) values.Add(values[^1]);

        var column = new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target, true), new SqlIdentifier(replacement.ForeignKeyColumn, true) }));
        var namer = new PolicyParameterNamer(FormattableString.Invariant($"__rls_ks{replacement.SiteOrdinal}_"));
        var list = AdvancedRlsPredicateBuilder.InLists(column, values, limit, namer);
        foreach (var (name, value) in namer.Values)
        {
            if (parameters.TryGetValue(name, out var existing) && (existing.Type != value.Type || !Equals(existing.Value, value.Value)))
            {
                throw new PolicyConflictException($"Policy parameter '{name}' is bound to conflicting values.");
            }

            parameters[name] = value;
        }

        return list;
    }
}
