namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Threading;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Issue #107 WP-B (PLAN-GOV-107 A-3, SEC-RF107-11): derives the key query of a registered correlated <c>EXISTS</c> and compiles it through
/// the same controls as a policy subquery. The derivation (single correlation conjunct, no remaining reference to the target, a
/// <c>dep.pk IS NOT NULL</c> conjunct) is checked structurally and every violation is a <see cref="KeyQueryNotDerivableException"/>. The
/// governance is <see cref="PolicySubqueryTenantRewriter"/> (unchanged: tenant wrapper per physical table, nested subqueries and unknown
/// tables rejected with a <see cref="SecurityException"/>), the coverage proof of the policy scope, the dialect capability validator and the
/// emitted-text invariant checker. The pipeline honours <see cref="CompilerHooks"/>, so the mutation tests can switch each control off.
/// A <see cref="SecurityException"/> or <see cref="SqlLimitExceededException"/> is never turned into a <see cref="KeyQueryNotDerivableException"/>.
/// </summary>
public sealed class PolicyKeyQueryCompiler : IPolicyKeyQueryCompiler
{
    /// <summary>Version of the derivation rules; part of <see cref="CompiledKeyQuery.CompilerVersion"/> and therefore of every cache key.</summary>
    public const string KeyQueryVersion = "key-query/1";

    private static readonly IReadOnlyDictionary<string, object?> NoClientValues = new Dictionary<string, object?>();

    public CompiledKeyQuery CompileKeyQuery(PolicyKeyQueryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Correlated);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        ArgumentNullException.ThrowIfNull(request.Catalog);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Tenant);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.RowLimit, 1);
        ct.ThrowIfCancellationRequested();

        var caps = Capabilities(request.Dialect);
        var derived = Derive(request.Correlated.Subquery);

        // The statement that is governed and emitted: the original FROM (every table is wrapped by the rewriter below), the key column,
        // dep.pk IS NOT NULL and the remaining conjuncts, a deterministic order and the row limit (MaxKeys + 1, overflow detection).
        var keyColumn = derived.KeyColumn;
        Expression where = new UnaryExpression(UnaryOperator.IsNotNull, keyColumn);
        foreach (var conjunct in derived.Remaining)
        {
            where = new BinaryExpression(where, BinaryOperator.And, conjunct);
        }

        var statement = new SelectStatement(
            null,
            new QuerySpecification(true, [new ColumnSelectItem(keyColumn, null)], derived.From, where, null, null),
            new OrderByClause([new OrderByElement(keyColumn)]),
            new PaginationClause(null, new LiteralExpression((long)request.RowLimit, LiteralType.Integer)));

        var typed = new TypedPolicyContext(request.Catalog, NoRowFilters.Instance, NoMasks.Instance, request.Tenant, caps);
        var governed = (SelectStatement)new PolicySubqueryTenantRewriter(typed) { CancellationToken = ct }.Visit(statement);

        // Rules that need the catalog or the values: only after the rewriter proved that every table resolves and nothing is nested.
        var (keyType, foreignKeyType) = ResolveTypes(request, derived);
        CheckBody(derived, request.Parameters);
        BindValues(governed, request.Parameters, typed);

        SqlStatement secured = CompilerHooks.Current.InterceptInjected(governed);
        AssertKeyStatement(secured, keyColumn, request.RowLimit);

        if (!CompilerHooks.IsDisabled(SecurityControl.CoverageVerifier))
        {
            typed.CreateVerifier(request.Dialect).Verify(PolicyScopeWrapper((SelectStatement)secured), ct);
        }

        DialectCapabilityValidator.Validate(secured, caps);

        var generator = SqlDialectGeneratorFactory.GetGenerator(request.Dialect);
        var values = new ParameterSource(typed.PolicyValues, NoClientValues);
        var compiled = generator is SqlDialectGeneratorBase capabilityAware
            ? capabilityAware.Generate(secured, values, caps, ct)
            : generator.Generate(secured, values, ct);
        if (compiled.StatementClass != SqlStatementClass.Select || compiled.Dialect != request.Dialect)
        {
            throw new SecurityException("The key query did not compile to a single SELECT.");
        }

        return new CompiledKeyQuery(
            compiled.Sql,
            compiled.Parameters,
            keyType,
            foreignKeyType,
            derived.ForeignKeyColumn,
            compiled.CompilerVersion + "+" + KeyQueryVersion);
    }

    private static DialectCapabilities Capabilities(TargetSqlDialect dialect)
    {
        DialectCapabilities caps;
        try
        {
            caps = DialectCapabilityTable.Default.Get(dialect);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw NotDerivable(KeyQueryNotDerivableReasons.DialectUnsupported, "Dialect");
        }

        return caps.Tier == DialectSupportTier.Production ? caps : throw NotDerivable(KeyQueryNotDerivableReasons.DialectUnsupported, "Dialect");
    }

    private static KeyQueryNotDerivableException NotDerivable(string reason, string rule) => new(reason, rule);

    private static KeyQueryNotDerivableException NotDerivable(string rule) => new(KeyQueryNotDerivableReasons.NotDerivable, rule);

    // ---- A-3 derivation rules ----

    private sealed record Derivation(
        ColumnReference KeyColumn,
        string KeyAlias,
        string ForeignKeyColumn,
        TableSource From,
        IReadOnlyList<Expression> Remaining);

    private static Derivation Derive(SelectStatement body)
    {
        // Rule 1: a plain SELECT ... FROM ... WHERE (no WITH, ORDER BY, LIMIT, GROUP BY, HAVING, set operation).
        if (body.With is not null) throw NotDerivable("With");
        if (body.OrderBy is not null) throw NotDerivable("OrderBy");
        if (body.Pagination is not null) throw NotDerivable("Limit");
        if (body.Body is not QuerySpecification spec) throw NotDerivable("BodyShape");
        if (spec.GroupBy is not null) throw NotDerivable("GroupBy");
        if (spec.Having is not null) throw NotDerivable("Having");
        if (spec.From is null) throw NotDerivable("NoFrom");
        if (spec.Where is null) throw NotDerivable("NoCorrelation");

        // WHERE is a conjunction (AND chain, parentheses allowed). An OR at the top level has no single correlation conjunct.
        if (StripParentheses(spec.Where) is BinaryExpression { Operator: BinaryOperator.Or }) throw NotDerivable("TopLevelOr");
        var conjuncts = Flatten(spec.Where);

        // Rule 2: exactly one conjunct is dep.pk = autheris_target.fk (two bare columns, one qualified with the reserved target alias).
        ColumnReference? key = null;
        ColumnReference? foreignKey = null;
        int index = -1;
        for (int i = 0; i < conjuncts.Count; i++)
        {
            if (StripParentheses(conjuncts[i]) is not BinaryExpression { Operator: BinaryOperator.Equal, Left: ColumnReference left, Right: ColumnReference right })
            {
                continue;
            }

            bool leftTarget = ReferencesTarget(left), rightTarget = ReferencesTarget(right);
            if (leftTarget == rightTarget)
            {
                continue;
            }

            var (dep, target) = leftTarget ? (right, left) : (left, right);
            if (target.Name.Parts.Count != 2 || dep.Name.Parts.Count != 2) throw NotDerivable("CorrelationOperand");
            if (key is not null) throw NotDerivable("MultipleCorrelation");
            (key, foreignKey, index) = (dep, target, i);
        }

        if (key is null || foreignKey is null)
        {
            // dep.pk = t.fk + 0 and every other correlation through an expression is not a column correlation.
            throw NotDerivable(conjuncts.Any(c => AstReflection.Collect<ColumnReference>(c).Any(ReferencesTarget)) ? "CorrelationNotColumn" : "NoCorrelation");
        }

        // Rule 3: nothing else may depend on the outer row (this is what makes EXISTS and IN equivalent), also not in the joins or aliases.
        var remaining = new List<Expression>(conjuncts.Count - 1);
        for (int i = 0; i < conjuncts.Count; i++)
        {
            if (i != index) remaining.Add(conjuncts[i]);
        }

        if (remaining.Any(c => AstReflection.Collect<ColumnReference>(c).Any(ReferencesTarget)) ||
            AstReflection.Collect<ColumnReference>(spec.From).Any(ReferencesTarget) ||
            HasTargetAlias(spec.From))
        {
            throw NotDerivable("TargetReferenceRemains");
        }

        return new Derivation(key, key.Name.Parts[0].Value, foreignKey.Name.Parts[^1].Value, spec.From, remaining);
    }

    private static Expression StripParentheses(Expression expression)
    {
        while (expression is ParenthesizedExpression parenthesized) expression = parenthesized.Expression;
        return expression;
    }

    /// <summary>The conjuncts of an AND chain in source order (parentheses around an AND are looked through, any other parenthesized expression is one conjunct).</summary>
    private static List<Expression> Flatten(Expression where)
    {
        var conjuncts = new List<Expression>();
        var stack = new Stack<Expression>();
        stack.Push(where);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (StripParentheses(current) is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                stack.Push(and.Right);
                stack.Push(and.Left);
            }
            else
            {
                conjuncts.Add(current);
            }
        }

        return conjuncts;
    }

    private static bool ReferencesTarget(ColumnReference column) =>
        column.Name.Parts.Any(p => string.Equals(p.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));

    private static bool HasTargetAlias(TableSource source) => source switch
    {
        NamedTableSource named => string.Equals(named.Alias?.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(named.Name.Parts[^1].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase),
        JoinedTableSource joined => HasTargetAlias(joined.Left) || HasTargetAlias(joined.Right),
        SubqueryTableSource subquery => string.Equals(subquery.Alias.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase),
        LateralTableSource lateral => string.Equals(lateral.Alias.Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    // ---- SEC-RF107-01 (a), SEC-RF107-07 (a), (c) ----

    private static (string? KeyType, string? ForeignKeyType) ResolveTypes(PolicyKeyQueryRequest request, Derivation derived)
    {
        var aliases = new Dictionary<string, NamedTableSource>(StringComparer.Ordinal);
        CollectAliases(derived.From, aliases);
        if (!aliases.TryGetValue(derived.KeyAlias, out var source))
        {
            throw NotDerivable("CorrelationAlias");
        }

        string keyColumn = derived.KeyColumn.Name.Parts[^1].Value;
        var entry = request.Catalog.Resolve(source.Name) ?? throw new SecurityException("A policy subquery references a table that is not in the catalog.");
        var key = entry.Columns.FirstOrDefault(c => string.Equals(c.Name, keyColumn, StringComparison.OrdinalIgnoreCase));
        var foreignKey = request.Target.Columns.FirstOrDefault(c => string.Equals(c.Name, derived.ForeignKeyColumn, StringComparison.OrdinalIgnoreCase));
        if (key is null || foreignKey is null ||
            !KeySetKeyTypes.IsBindablePair(request.Dialect, key.DataType, foreignKey.DataType, key.Collation, foreignKey.Collation))
        {
            throw NotDerivable(KeyQueryNotDerivableReasons.TypeNotBindable, "KeyTypePair");
        }

        return (key.DataType, foreignKey.DataType);
    }

    private static void CollectAliases(TableSource source, Dictionary<string, NamedTableSource> into)
    {
        switch (source)
        {
            case NamedTableSource named:
                into[named.Alias?.Value ?? named.Name.Parts[^1].Value] = named;
                break;
            case JoinedTableSource joined:
                CollectAliases(joined.Left, into);
                CollectAliases(joined.Right, into);
                break;
        }
    }

    /// <summary>
    /// SEC-RF107-01 (a): every value of the body is a policy parameter of the filter definition (<see cref="ParameterOrigin.Policy"/> with a
    /// value of its declared type in the request); a client parameter, another origin or a node that can depend on the caller or on time
    /// makes the filter not derivable, so one principal's key set can never serve another.
    /// </summary>
    private static void CheckBody(Derivation derived, IReadOnlyDictionary<string, PolicyValue> values)
    {
        void Check(object node)
        {
            AstReflection.Walk(node, visited =>
            {
                switch (visited)
                {
                    case PolicyParameterExpression { Origin: not ParameterOrigin.Policy }:
                        throw NotDerivable("ParameterOrigin");
                    case PolicyParameterExpression parameter:
                        if (!values.TryGetValue(parameter.Name, out var value) || value.Type != parameter.Type) throw NotDerivable("ParameterValue");
                        break;
                    case ParameterReference or NamedBindMarkerExpression:
                        throw NotDerivable("ClientParameter");
                    case ColumnReference or LiteralExpression or TypedLiteralExpression or IntervalLiteralExpression or ParenthesizedExpression
                        or BinaryExpression or UnaryExpression or LikeExpression or InListExpression or BetweenExpression or IsDistinctFromExpression
                        or CaseExpression or WhenClause:
                        break;
                    case Expression:
                        throw NotDerivable("UnsupportedNode");
                }

                return true;
            });
        }

        Check(derived.From);
        foreach (var conjunct in derived.Remaining) Check(conjunct);
    }

    private static void BindValues(SelectStatement governed, IReadOnlyDictionary<string, PolicyValue> values, TypedPolicyContext typed)
    {
        foreach (var parameter in AstReflection.Collect<PolicyParameterExpression>(governed))
        {
            if (parameter.Origin == ParameterOrigin.Policy)
            {
                typed.AddValue(parameter.Name, values[parameter.Name]);
            }
        }
    }

    // ---- SEC-RF107-11: the result is re-checked after the injector hook, so a mutated pipeline cannot slip through ----

    private static void AssertKeyStatement(SqlStatement secured, ColumnReference keyColumn, int rowLimit)
    {
        if (secured is not SelectStatement { With: null, Body: QuerySpecification spec } select ||
            spec.Projections.Count != 1 || spec.Projections[0] is not ColumnSelectItem { Expression: var projected } ||
            !AstReflection.StructurallyEqual(projected, keyColumn))
        {
            throw new SecurityException("The key query is not a single SELECT of the key column.");
        }

        if (AstReflection.Collect<ColumnReference>(secured).Any(ReferencesTarget))
        {
            throw new SecurityException("The key query still references the target table.");
        }

        if (spec.Where is null || !Flatten(spec.Where).Any(c =>
                StripParentheses(c) is UnaryExpression { Operator: UnaryOperator.IsNotNull, Operand: var operand } &&
                AstReflection.StructurallyEqual(operand, keyColumn)))
        {
            throw new SecurityException("The key query lost its IS NOT NULL conjunct.");
        }

        if (select.Pagination is not { Offset: null, Limit: LiteralExpression { Type: LiteralType.Integer, Value: long limit } } || limit != rowLimit)
        {
            throw new SecurityException("The key query lost its row limit.");
        }
    }

    /// <summary>
    /// <c>SELECT 1 WHERE &lt;policy predicate: EXISTS (key query)&gt;</c>: the coverage verifier proves the policy scope (a tenant predicate on every
    /// physical table, no nesting) only below a security predicate. The row limit is a literal and is checked by <see cref="AssertKeyStatement"/>.
    /// </summary>
    private static SelectStatement PolicyScopeWrapper(SelectStatement key) => new(
        null,
        new QuerySpecification(
            false,
            [new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null)],
            null,
            new SecurityPredicateExpression(
                new ExistsExpression(key with { Pagination = null }),
                new SecurityPredicateId("key-query", 0),
                SecurityScope.ExistsSubquery),
            null,
            null),
        null,
        null);

    /// <summary>No row policy applies to the tables of a policy subquery (they get the tenant predicate only).</summary>
    private sealed class NoRowFilters : IPolicyPredicateProvider
    {
        public static NoRowFilters Instance { get; } = new();

        public bool ShouldApplyPolicy(TableIdentity table) => false;

        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class NoMasks : IColumnMaskProvider
    {
        public static NoMasks Instance { get; } = new();

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException("A policy subquery has no masks.");
    }
}
