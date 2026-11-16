using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the per-cell flag decomposition shared by the security injector and the coverage verifier: which
/// correlated EXISTS scopes become deduplicated flag joins, which stay untouched (fail closed: the EXISTS is kept verbatim), and the
/// derived clear/mask branch conditions and the target-reference flag.
/// </summary>
[Trait("Category", "SqlGate")]
public class PerCellFlagDecompositionTests
{
    private static readonly TableIdentity OrdersId = new("dbo", "orders");

    private static readonly TableCatalogEntry Entry = new(
        OrdersId,
        ImmutableArray.Create(new CatalogColumn("id", "int"), new CatalogColumn("region", "varchar(32)"), new CatalogColumn("owner", "varchar(32)")),
        null,
        1L);

    private static SelectStatement Select(string sql) => (SelectStatement)CompilerTestHelpers.Build(sql);

    private static PolicyPredicate Policy(Expression expression) => PolicyPredicate.Create(expression, new Dictionary<string, PolicyValue>());

    private static PolicyPredicate ExistsPolicy(string subquerySql) => Policy(new ExistsExpression(Select(subquerySql)));

    private static PerCellTablePolicy Policy(
        IReadOnlyList<PerCellSignatureSpec> signatures,
        IReadOnlyDictionary<string, PerCellColumnRule>? columns = null,
        PolicyPredicate? narrowing = null) =>
        new(signatures, columns ?? new Dictionary<string, PerCellColumnRule>(), narrowing);

    private static PerCellFlagDecomposition.Result Decompose(PerCellTablePolicy policy) => PerCellFlagDecomposition.Decompose(policy, Entry);

    private const string Correlated = "SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'x'";

    [Fact]
    public void NullArguments_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => PerCellFlagDecomposition.Decompose(null!, Entry));
        Assert.Throws<ArgumentNullException>(() => PerCellFlagDecomposition.Decompose(Policy([]), null!));
    }

    [Fact]
    public void UnfilteredSignature_BecomesATautologyAndNoGrantUnion()
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(Correlated), IsUnfiltered: true)]));

        Assert.Empty(result.FlagJoins);
        var flag = Assert.IsType<BinaryExpression>(Assert.Single(result.SignatureFlags));
        Assert.Equal(BinaryOperator.Equal, flag.Operator);
        Assert.Equal(1L, Assert.IsType<LiteralExpression>(flag.Left).Value);
        Assert.Equal(1L, Assert.IsType<LiteralExpression>(flag.Right).Value);
        Assert.Null(result.GrantUnionPredicate);
        Assert.False(result.ReferencesTarget);
    }

    [Fact]
    public void CorrelatedExists_BecomesADistinctKeyFlagJoin()
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(Correlated))]));

        var join = Assert.Single(result.FlagJoins);
        Assert.Equal("__f_0", join.Alias.Value);
        Assert.True(join.Alias.IsQuoted);
        Assert.Equal(new[] { "id" }, join.TargetColumns);

        var inner = Assert.IsType<QuerySpecification>(join.Subquery.Body);
        Assert.True(inner.Distinct);
        var key = Assert.IsType<ColumnSelectItem>(Assert.Single(inner.Projections));
        Assert.Equal("__autheris_fk_0", key.Alias!.Value);
        Assert.True(key.Alias.IsQuoted);
        Assert.Equal("oid", Assert.IsType<ColumnReference>(key.Expression).Name.Parts[^1].Value);
        Assert.Null(inner.GroupBy);
        Assert.Null(inner.Having);
        Assert.Null(join.Subquery.With);
        Assert.Null(join.Subquery.OrderBy);
        Assert.Null(join.Subquery.Pagination);

        // the remaining filter and the key's NOT NULL guard are the only conjuncts
        var where = Assert.IsType<BinaryExpression>(inner.Where);
        Assert.Equal(BinaryOperator.And, where.Operator);
        var guard = Assert.IsType<UnaryExpression>(where.Right);
        Assert.Equal(UnaryOperator.IsNotNull, guard.Operator);
        Assert.Equal("oid", Assert.IsType<ColumnReference>(guard.Operand).Name.Parts[^1].Value);
        var rest = Assert.IsType<BinaryExpression>(where.Left);
        Assert.Equal(BinaryOperator.Equal, rest.Operator);
        Assert.Equal("tag", Assert.IsType<ColumnReference>(rest.Left).Name.Parts[^1].Value);

        // ON <flag>.__autheris_fk_0 = autheris_target.<canonical target column>
        var on = Assert.IsType<BinaryExpression>(join.OnCondition.Predicate);
        Assert.Equal(BinaryOperator.Equal, on.Operator);
        var onLeft = Assert.IsType<ColumnReference>(on.Left).Name.Parts;
        Assert.Equal(new[] { "__f_0", "__autheris_fk_0" }, onLeft.Select(p => p.Value));
        var onRight = Assert.IsType<ColumnReference>(on.Right).Name.Parts;
        Assert.Equal(new[] { RowFilterAliases.Target, "id" }, onRight.Select(p => p.Value));
        Assert.True(onRight[1].IsQuoted);
        Assert.False(onRight[0].IsQuoted);

        // the signature flag is the key probe of the flag join
        var flag = Assert.IsType<UnaryExpression>(Assert.Single(result.SignatureFlags));
        Assert.Equal(UnaryOperator.IsNotNull, flag.Operator);
        Assert.Equal(new[] { "__f_0", "__autheris_fk_0" }, Assert.IsType<ColumnReference>(flag.Operand).Name.Parts.Select(p => p.Value));
        Assert.True(result.ReferencesTarget);
        Assert.NotNull(result.GrantUnionPredicate);
    }

    [Fact]
    public void TargetColumnSpelling_ResolvesToTheCanonicalCatalogName()
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE autheris_target.REGION = a.r"))]));

        var join = Assert.Single(result.FlagJoins);
        Assert.Equal(new[] { "region" }, join.TargetColumns);
        var inner = Assert.IsType<QuerySpecification>(join.Subquery.Body);
        // no remaining filter: the guard is the whole WHERE
        var guard = Assert.IsType<UnaryExpression>(inner.Where);
        Assert.Equal(UnaryOperator.IsNotNull, guard.Operator);
    }

    [Fact]
    public void TwoKeyColumns_ProduceTwoOrderedKeysAndJoinConditions()
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(
            "SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND autheris_target.owner = a.who AND a.tag = 'x'"))]));

        var join = Assert.Single(result.FlagJoins);
        Assert.Equal(new[] { "id", "owner" }, join.TargetColumns);
        var inner = Assert.IsType<QuerySpecification>(join.Subquery.Body);
        Assert.Equal(new[] { "__autheris_fk_0", "__autheris_fk_1" }, inner.Projections.Cast<ColumnSelectItem>().Select(p => p.Alias!.Value));
        var on = Assert.IsType<BinaryExpression>(join.OnCondition.Predicate);
        Assert.Equal(BinaryOperator.And, on.Operator);
        var first = Assert.IsType<BinaryExpression>(on.Left);
        var second = Assert.IsType<BinaryExpression>(on.Right);
        Assert.Equal("__autheris_fk_0", Assert.IsType<ColumnReference>(first.Left).Name.Parts[1].Value);
        Assert.Equal("id", Assert.IsType<ColumnReference>(first.Right).Name.Parts[1].Value);
        Assert.Equal("__autheris_fk_1", Assert.IsType<ColumnReference>(second.Left).Name.Parts[1].Value);
        Assert.Equal("owner", Assert.IsType<ColumnReference>(second.Right).Name.Parts[1].Value);
        // the probe uses the first key only
        var flag = Assert.IsType<UnaryExpression>(Assert.Single(result.SignatureFlags));
        Assert.Equal("__autheris_fk_0", Assert.IsType<ColumnReference>(flag.Operand).Name.Parts[1].Value);
    }

    [Fact]
    public void IdenticalExistsScopes_ShareOneFlagJoin_AndDifferentOnesGetTheNextAlias()
    {
        var result = Decompose(Policy(
        [
            new PerCellSignatureSpec(ExistsPolicy(Correlated)),
            new PerCellSignatureSpec(ExistsPolicy(Correlated)),
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'y'")),
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.owner AND a.tag = 'x'"))
        ]));

        Assert.Equal(new[] { "__f_0", "__f_1", "__f_2" }, result.FlagJoins.Select(j => j.Alias.Value));
        var probes = result.SignatureFlags.Select(f => Assert.IsType<ColumnReference>(Assert.IsType<UnaryExpression>(f).Operand).Name.Parts[0].Value).ToArray();
        Assert.Equal(new[] { "__f_0", "__f_0", "__f_1", "__f_2" }, probes);
    }

    [Fact]
    public void SameSubqueryOverDifferentTargetColumns_IsNotDeduplicated()
    {
        var result = Decompose(Policy(
        [
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id")),
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.region"))
        ]));

        Assert.Equal(2, result.FlagJoins.Count);
        Assert.Equal(new[] { "id" }, result.FlagJoins[0].TargetColumns);
        Assert.Equal(new[] { "region" }, result.FlagJoins[1].TargetColumns);
    }

    [Theory]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id OR a.tag = 'x'")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE (a.oid = autheris_target.id OR a.tag = 'x')")]
    [InlineData("SELECT 1 FROM conf.acl a")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.tag = 'x'")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id GROUP BY a.oid")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id GROUP BY a.oid HAVING COUNT(*) > 1")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id ORDER BY a.oid")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id LIMIT 1")]
    [InlineData("WITH w AS (SELECT 1 AS one) SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id UNION ALL SELECT 1 FROM conf.acl b WHERE b.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.nosuchcolumn")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE autheris_target.id = autheris_target.region")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = a.other")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid > autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.x = autheris_target.region + 1")]
    [InlineData("SELECT 1 FROM conf.acl a JOIN conf.other o ON o.id = autheris_target.id WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl autheris_target WHERE autheris_target.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a JOIN conf.other autheris_target ON a.id = autheris_target.id WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a JOIN (SELECT 1 AS id FROM conf.t) autheris_target ON a.id = autheris_target.id WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM (SELECT 1 AS oid FROM conf.acl) autheris_target WHERE autheris_target.oid = 5 AND 1 = autheris_target.oid")]
    public void NotDecomposable_KeepsTheExistsVerbatim(string subquerySql)
    {
        var predicate = ExistsPolicy(subquerySql);
        var result = Decompose(Policy([new PerCellSignatureSpec(predicate)]));

        Assert.Empty(result.FlagJoins);
        var flag = Assert.IsType<ExistsExpression>(Assert.Single(result.SignatureFlags));
        Assert.Same(((ExistsExpression)predicate.Expression).Subquery, flag.Subquery);
    }

    [Theory]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.p IN (SELECT 1 FROM x WHERE x.k = autheris_target.id)")]
    [InlineData("SELECT 1 FROM conf.acl a JOIN conf.o o ON o.k = autheris_target.owner WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a JOIN (SELECT 1 AS k FROM conf.o autheris_target) s ON s.k = a.oid WHERE a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM (SELECT 1 AS k FROM conf.o autheris_target) s WHERE s.k = autheris_target.id")]
    [InlineData("WITH c AS (SELECT 1 AS k FROM conf.o autheris_target) SELECT 1 FROM (SELECT 1 AS k FROM conf.acl) s WHERE s.k = autheris_target.id")]
    [InlineData("SELECT 1 FROM (SELECT 1 AS k FROM conf.o UNION ALL SELECT 2 FROM conf.o2 autheris_target) s WHERE s.k = autheris_target.id")]
    public void LeftoverTargetReferences_KeepTheExistsVerbatim(string subquerySql)
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(subquerySql))]));

        Assert.Empty(result.FlagJoins);
        Assert.IsType<ExistsExpression>(Assert.Single(result.SignatureFlags));
    }

    [Theory]
    [InlineData("SELECT 1 FROM conf.acl a WHERE autheris_target.id = a.oid")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE (a.oid = autheris_target.id)")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE (a.tag = 'x') AND (a.oid = autheris_target.id)")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE ((a.tag = 'x' AND a.oid = autheris_target.id))")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.tag = 'x' AND a.k > 1 AND a.oid = autheris_target.id")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'x' AND a.k > 1")]
    [InlineData("SELECT 1 FROM conf.acl a WHERE oid = autheris_target.id")]
    public void CorrelationSpellings_AreDecomposed(string subquerySql)
    {
        var result = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(subquerySql))]));

        var join = Assert.Single(result.FlagJoins);
        Assert.Equal(new[] { "id" }, join.TargetColumns);
        Assert.IsType<UnaryExpression>(Assert.Single(result.SignatureFlags));
    }

    [Fact]
    public void ConjunctionOfExistsAndOtherTerms_IsRewrittenInPlace()
    {
        var predicate = Policy(new BinaryExpression(
            new ParenthesizedExpression(new UnaryExpression(UnaryOperator.Not, new ExistsExpression(Select(Correlated)))),
            BinaryOperator.Or,
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("region", true)])),
                BinaryOperator.Equal,
                new LiteralExpression("EU", LiteralType.String))));
        var result = Decompose(Policy([new PerCellSignatureSpec(predicate)]));

        Assert.Single(result.FlagJoins);
        var or = Assert.IsType<BinaryExpression>(Assert.Single(result.SignatureFlags));
        Assert.Equal(BinaryOperator.Or, or.Operator);
        var paren = Assert.IsType<ParenthesizedExpression>(or.Left);
        var not = Assert.IsType<UnaryExpression>(paren.Expression);
        Assert.Equal(UnaryOperator.Not, not.Operator);
        Assert.Equal(UnaryOperator.IsNotNull, Assert.IsType<UnaryExpression>(not.Operand).Operator);
        Assert.IsType<BinaryExpression>(or.Right);
    }

    [Fact]
    public void RewriteHooks_AreAppliedToTheFlagAndTheFlagSubquery()
    {
        int expressions = 0;
        int subqueries = 0;
        var result = PerCellFlagDecomposition.Decompose(
            Policy([new PerCellSignatureSpec(ExistsPolicy(Correlated)), new PerCellSignatureSpec(ExistsPolicy(Correlated))]),
            Entry,
            e => { expressions++; return e; },
            s => { subqueries++; return s; });

        Assert.Equal(2, expressions);
        Assert.Equal(1, subqueries);
        Assert.Single(result.FlagJoins);
    }

    [Fact]
    public void RewriteExpressionHook_ReplacesTheSignatureFlag()
    {
        var marker = new LiteralExpression(true, LiteralType.Boolean);
        var result = PerCellFlagDecomposition.Decompose(
            Policy([new PerCellSignatureSpec(ExistsPolicy(Correlated))]), Entry, _ => marker);

        Assert.Same(marker, Assert.Single(result.SignatureFlags));
    }

    [Fact]
    public void ColumnBranches_OrChainTheSignatureFlagsAndFingerprintThem()
    {
        var result = Decompose(Policy(
            [
                new PerCellSignatureSpec(ExistsPolicy(Correlated)),
                new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'y'")),
                new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'z'"))
            ],
            new Dictionary<string, PerCellColumnRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["Email"] = new(ImmutableArray.Create(0, 2), ImmutableArray.Create(1)),
                ["phone"] = new(ImmutableArray<int>.Empty, ImmutableArray.Create(0)),
                ["note"] = new(ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)
            }));

        var email = result.Columns["email"];
        var clear = Assert.IsType<BinaryExpression>(email.ClearCondition);
        Assert.Equal(BinaryOperator.Or, clear.Operator);
        Assert.Same(result.SignatureFlags[0], clear.Left);
        Assert.Same(result.SignatureFlags[2], clear.Right);
        Assert.Same(result.SignatureFlags[1], email.MaskCondition);
        Assert.Equal(TrinoSqlEngine.Ast.Security.AstReflection.Fingerprint(clear), email.ClearConditionFingerprint);
        Assert.Equal(TrinoSqlEngine.Ast.Security.AstReflection.Fingerprint(result.SignatureFlags[1]), email.MaskConditionFingerprint);

        var phone = result.Columns["phone"];
        Assert.Null(phone.ClearCondition);
        Assert.Null(phone.ClearConditionFingerprint);
        Assert.Same(result.SignatureFlags[0], phone.MaskCondition);
        Assert.NotNull(phone.MaskConditionFingerprint);

        var note = result.Columns["note"];
        Assert.Null(note.ClearCondition);
        Assert.Null(note.MaskCondition);
        Assert.Null(note.ClearConditionFingerprint);
        Assert.Null(note.MaskConditionFingerprint);
    }

    [Fact]
    public void GrantUnion_OrChainsEverySignatureInOrder_UnlessOneIsUnfiltered()
    {
        var three = Decompose(Policy(
        [
            new PerCellSignatureSpec(ExistsPolicy(Correlated)),
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'y'")),
            new PerCellSignatureSpec(ExistsPolicy("SELECT 1 FROM conf.acl a WHERE a.oid = autheris_target.id AND a.tag = 'z'"))
        ]));
        var outer = Assert.IsType<BinaryExpression>(three.GrantUnionPredicate);
        Assert.Equal(BinaryOperator.Or, outer.Operator);
        Assert.Same(three.SignatureFlags[2], outer.Right);
        var inner = Assert.IsType<BinaryExpression>(outer.Left);
        Assert.Same(three.SignatureFlags[0], inner.Left);
        Assert.Same(three.SignatureFlags[1], inner.Right);

        var withUnfiltered = Decompose(Policy([new PerCellSignatureSpec(ExistsPolicy(Correlated)), new PerCellSignatureSpec(ExistsPolicy(Correlated), IsUnfiltered: true)]));
        Assert.Null(withUnfiltered.GrantUnionPredicate);

        var none = Decompose(Policy([]));
        Assert.Null(none.GrantUnionPredicate);
        Assert.Empty(none.SignatureFlags);
        Assert.False(none.ReferencesTarget);
    }

    [Fact]
    public void ReferencesTarget_CoversFlagsAndTheNarrowingPredicate()
    {
        var targetFilter = Policy(new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("region", true)])),
            BinaryOperator.Equal,
            new LiteralExpression("EU", LiteralType.String)));
        var plainFilter = Policy(new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("region", true)])),
            BinaryOperator.Equal,
            new LiteralExpression("EU", LiteralType.String)));
        var unaliasedOther = Policy(new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("other"), new SqlIdentifier("region", true)])),
            BinaryOperator.Equal,
            new LiteralExpression("EU", LiteralType.String)));

        Assert.True(Decompose(Policy([new PerCellSignatureSpec(targetFilter)])).ReferencesTarget);
        Assert.False(Decompose(Policy([new PerCellSignatureSpec(plainFilter)])).ReferencesTarget);
        Assert.False(Decompose(Policy([new PerCellSignatureSpec(unaliasedOther)])).ReferencesTarget);
        Assert.True(Decompose(Policy([new PerCellSignatureSpec(plainFilter)], null, targetFilter)).ReferencesTarget);
        Assert.False(Decompose(Policy([new PerCellSignatureSpec(plainFilter)], null, plainFilter)).ReferencesTarget);
        Assert.True(PerCellFlagDecomposition.ReferencesTargetAlias(targetFilter.Expression));
        Assert.True(PerCellFlagDecomposition.ReferencesTargetAlias(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier("AUTHERIS_TARGET"), new SqlIdentifier("x")]))));
        Assert.False(PerCellFlagDecomposition.ReferencesTargetAlias(plainFilter.Expression));
        Assert.False(PerCellFlagDecomposition.ReferencesTargetAlias(unaliasedOther.Expression));
    }
}
