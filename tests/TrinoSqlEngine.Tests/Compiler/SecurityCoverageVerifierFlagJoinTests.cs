using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using Xunit;
using static TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the verifier's per-cell flag join rules: a secured derived table may carry flag joins only when its
/// requirement expects exactly that many, each flag join must be a plain DISTINCT subquery that is itself secured, and its ON condition
/// may only equate flag keys with catalog columns of the target. The verifier never trusts the injector, so a tampered join (an inner
/// join, an extra conjunct, a foreign key column) must be refused with its own reason.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class SecurityCoverageVerifierFlagJoinTests
{
    private static readonly SecurityPredicateId RootId = new("dbo.p", 0);
    private static readonly SecurityPredicateId TenantId = new("dbo.u", 0);

    private const string UnpermittedJoins = "A secured derived table contains unpermitted flag joins.";
    private const string MissingJoins = "A per-cell secured derived table is missing required flag joins.";
    private const string NotPlain = "A per-cell flag join must be a plain SELECT DISTINCT subquery.";
    private const string BadOn = "A per-cell flag join ON condition must equate flag keys to target columns.";

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, true)).ToArray()));

    private static SqlQualifiedName Canonical(string table) => new(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier(table, true) });

    private static Expression Pred(SecurityPredicateId id) =>
        new SecurityPredicateExpression(new BinaryExpression(Col("tenant"), Equal, new PolicyParameterExpression("t", SqlParameterType.String)), id, SecurityScope.Root);

    private static SecurityCoverageVerifier Verifier(int? expectedFlagJoins = 1, ImmutableHashSet<string>? targetColumns = null, bool dependentNeedsTenant = false) => new(name =>
    {
        if (name.Parts.Count == 0) return null;
        return name.SimpleName.ToLowerInvariant() switch
        {
            "p" => new TableCoverageRequirement("dbo.p", "dbo", "p", ImmutableArray.Create(RootId), ImmutableArray<SecurityPredicateId>.Empty,
                CatalogColumns: targetColumns ?? ImmutableHashSet.Create("id", "owner"),
                PerCell: expectedFlagJoins is { } n
                    ? new PerCellTableCoverage(false, false, ImmutableDictionary<string, PerCellColumnCoverage>.Empty, n)
                    : null),
            "u" => new TableCoverageRequirement("dbo.u", "dbo", "u", ImmutableArray.Create(TenantId),
                dependentNeedsTenant ? ImmutableArray.Create(TenantId) : ImmutableArray<SecurityPredicateId>.Empty, CatalogColumns: ImmutableHashSet.Create("id")),
            _ => null
        };
    });

    private static SubqueryTableSource FlagSub(
        string alias = "__f_0",
        bool distinct = true,
        Expression? where = null,
        GroupByClause? groupBy = null,
        Expression? having = null,
        OrderByClause? orderBy = null,
        PaginationClause? pagination = null,
        WithClause? with = null)
    {
        // the flag subquery selects DISTINCT keys straight over the dependent table
        var inner = new QuerySpecification(distinct, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("__autheris_fk_0", true)) },
            new NamedTableSource(Canonical("u"), null), where, groupBy, having);
        return new SubqueryTableSource(new SelectStatement(with, inner, orderBy, pagination), new SqlIdentifier(alias, true));
    }

    private static Expression On(string flagAlias = "__f_0", string key = "__autheris_fk_0", string targetAlias = "t", string column = "id") =>
        new BinaryExpression(Col(flagAlias, key), Equal, Col(targetAlias, column));

    private static SelectStatement Statement(
        TableSource? joins = null,
        Expression? on = null,
        JoinType type = JoinType.LeftOuter,
        SubqueryTableSource? flag = null,
        string targetAlias = "t")
    {
        TableSource from = joins ?? new JoinedTableSource(
            new NamedTableSource(Canonical("p"), new SqlIdentifier(targetAlias, true)),
            type,
            flag ?? FlagSub(),
            new OnJoinCondition(on ?? On(targetAlias: targetAlias)));
        var secured = new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) }, from, Pred(RootId), null, null);
        var derived = new SubqueryTableSource(new SelectStatement(null, secured, null, null), new SqlIdentifier("p", true));
        return new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, derived, null, null, null), null, null);
    }

    private static void AssertRejected(SqlStatement statement, string message, SecurityCoverageVerifier? verifier = null) =>
        Assert.Equal(message, Assert.Throws<SecurityCoverageException>(() => (verifier ?? Verifier()).Verify(statement, CancellationToken.None)).Message);

    [Fact]
    public void IntactFlagJoin_IsAccepted()
    {
        Assert.Contains(RootId, Verifier().Verify(Statement(), CancellationToken.None));
    }

    [Fact]
    public void FlagJoinCount_MustEqualTheRequirement()
    {
        AssertRejected(Statement(), UnpermittedJoins, Verifier(expectedFlagJoins: 2));
        AssertRejected(Statement(), UnpermittedJoins, Verifier(expectedFlagJoins: 0));
        AssertRejected(Statement(), UnpermittedJoins, Verifier(expectedFlagJoins: null));
    }

    [Fact]
    public void MissingFlagJoins_AreRejectedWhenTheRequirementExpectsThem()
    {
        var plain = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new SubqueryTableSource(new SelectStatement(null,
                    new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) },
                        new NamedTableSource(Canonical("p"), null), Pred(RootId), null, null), null, null), new SqlIdentifier("p", true)),
                null, null, null), null, null);

        AssertRejected(plain, MissingJoins, Verifier(expectedFlagJoins: 1));
        Assert.Contains(RootId, Verifier(expectedFlagJoins: 0).Verify(plain, CancellationToken.None));
        Assert.Contains(RootId, Verifier(expectedFlagJoins: null).Verify(plain, CancellationToken.None));
    }

    [Theory]
    [InlineData(JoinType.Inner)]
    [InlineData(JoinType.RightOuter)]
    [InlineData(JoinType.FullOuter)]
    public void OnlyLeftOuterJoins_AreFlagJoins(JoinType type)
    {
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(type: type), CancellationToken.None));
    }

    [Fact]
    public void FlagJoinAliasMustCarryTheFlagPrefix()
    {
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(flag: FlagSub(alias: "f_0")), CancellationToken.None));
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(flag: FlagSub(alias: "_f_0")), CancellationToken.None));
        Assert.Contains(RootId, Verifier().Verify(Statement(flag: FlagSub(alias: "__f_17"), on: On(flagAlias: "__f_17")), CancellationToken.None));
    }

    [Fact]
    public void FlagJoinWithoutOnCondition_IsRejected()
    {
        var noCondition = new JoinedTableSource(new NamedTableSource(Canonical("p"), new SqlIdentifier("t", true)), JoinType.LeftOuter, FlagSub(), null);
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(joins: noCondition), CancellationToken.None));

        var usingJoin = new JoinedTableSource(new NamedTableSource(Canonical("p"), new SqlIdentifier("t", true)), JoinType.LeftOuter, FlagSub(),
            new UsingJoinCondition(new[] { new SqlIdentifier("id", true) }));
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(joins: usingJoin), CancellationToken.None));
    }

    [Fact]
    public void FlagJoinOverAPlainTable_IsRejected()
    {
        var plainTable = new JoinedTableSource(new NamedTableSource(Canonical("p"), new SqlIdentifier("t", true)), JoinType.LeftOuter,
            new NamedTableSource(Canonical("u"), new SqlIdentifier("__f_0", true)), new OnJoinCondition(On()));
        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(Statement(joins: plainTable), CancellationToken.None));
    }

    [Fact]
    public void FlagSubquery_MustBeAPlainDistinctSelect()
    {
        AssertRejected(Statement(flag: FlagSub(distinct: false)), NotPlain);
        AssertRejected(Statement(flag: FlagSub(groupBy: new GroupByClause(new Expression[] { Col("id") }))), NotPlain);
        AssertRejected(Statement(flag: FlagSub(having: new BinaryExpression(Col("id"), Equal, new LiteralExpression(1L, LiteralType.Integer)))), NotPlain);
        AssertRejected(Statement(flag: FlagSub(orderBy: new OrderByClause(new[] { new OrderByElement(Col("id")) }))), NotPlain);
        AssertRejected(Statement(flag: FlagSub(pagination: new PaginationClause(null, new LiteralExpression(1L, LiteralType.Integer)))), NotPlain);
        AssertRejected(Statement(flag: FlagSub(with: new WithClause(false, Array.Empty<CommonTableExpression>()))), NotPlain);
    }

    [Fact]
    public void FlagSubquery_MustBeSecuredLikeAPolicySubquery()
    {
        // a dependent table that needs its tenant predicate cannot be the plain source of a DISTINCT flag subquery
        Assert.Throws<SecurityCoverageException>(() => Verifier(dependentNeedsTenant: true).Verify(Statement(flag: FlagSub()), CancellationToken.None));
    }

    [Theory]
    [InlineData("__g_0", "__autheris_fk_0", "t", "id")]
    [InlineData("__f_0", "fk_0", "t", "id")]
    [InlineData("__f_0", "autheris_fk_0", "t", "id")]
    [InlineData("__f_0", "__autheris_fk_0", "x", "id")]
    [InlineData("__f_0", "__autheris_fk_0", "t", "missing")]
    public void FlagJoinOn_MustEquateAFlagKeyWithACatalogColumnOfTheTarget(string flagAlias, string key, string targetAlias, string column)
    {
        var on = new BinaryExpression(Col(flagAlias, key), Equal, Col(targetAlias, column));
        AssertRejected(Statement(on: on), BadOn);
    }

    [Fact]
    public void FlagJoinOn_RejectsEveryOtherShape()
    {
        var literal = new LiteralExpression(1L, LiteralType.Integer);
        var good = On();
        AssertRejected(Statement(on: new BinaryExpression(Col("__f_0", "__autheris_fk_0"), NotEqual, Col("t", "id"))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(Col("t", "id"), Equal, Col("__f_0", "__autheris_fk_0"))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(Col("__f_0", "__autheris_fk_0"), Equal, literal)), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(Col("__autheris_fk_0"), Equal, Col("t", "id"))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(Col("__f_0", "__autheris_fk_0"), Equal, Col("id"))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(Col("dbo", "__f_0", "__autheris_fk_0"), Equal, Col("t", "id"))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(good, Or, good)), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(good, And, new BinaryExpression(literal, Equal, literal))), BadOn);
        AssertRejected(Statement(on: new BinaryExpression(new BinaryExpression(literal, Equal, literal), And, good)), BadOn);
        AssertRejected(Statement(on: literal), BadOn);
    }

    [Fact]
    public void FlagJoinOn_AcceptsConjunctionsAndParenthesesOfKeyEqualities()
    {
        var two = new BinaryExpression(On(), And, new BinaryExpression(Col("__f_0", "__autheris_fk_1"), Equal, Col("t", "owner")));
        Assert.Contains(RootId, Verifier().Verify(Statement(on: two), CancellationToken.None));
        Assert.Contains(RootId, Verifier().Verify(Statement(on: new ParenthesizedExpression(two)), CancellationToken.None));
        Assert.Contains(RootId, Verifier().Verify(Statement(on: new BinaryExpression(new ParenthesizedExpression(On()), And, new ParenthesizedExpression(On()))), CancellationToken.None));
        // case differences of the target alias are tolerated, those of the flag alias are not
        Assert.Contains(RootId, Verifier().Verify(Statement(on: On(targetAlias: "T")), CancellationToken.None));
        AssertRejected(Statement(on: On(flagAlias: "__F_0")), BadOn);
        // without a catalog column set every target column of the alias is accepted
        Assert.Contains(RootId, Verifier(targetColumns: null).Verify(Statement(on: On(column: "owner")), CancellationToken.None));
    }

    [Fact]
    public void FlagJoinOn_RejectsAnUnaliasedTarget()
    {
        var unaliased = new JoinedTableSource(new NamedTableSource(Canonical("p"), null), JoinType.LeftOuter, FlagSub(), new OnJoinCondition(On()));
        AssertRejected(Statement(joins: unaliased), BadOn);
    }

    [Fact]
    public void FlagJoinsAreOnlyPermittedAtTheSecuredTableNotInAPolicyScope()
    {
        // the flag join sits inside a policy subquery of another secured table: policy scope never carries flag joins
        var inner = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) },
                new JoinedTableSource(new NamedTableSource(Canonical("p"), new SqlIdentifier("t", true)), JoinType.LeftOuter, FlagSub(), new OnJoinCondition(On())),
                Pred(RootId), null, null), null, null);
        var exists = new ExistsExpression(inner);
        var host = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new SubqueryTableSource(new SelectStatement(null,
                    new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) },
                        new NamedTableSource(Canonical("u"), null), new BinaryExpression(Pred(TenantId), And, new SecurityPredicateExpression(exists, new SecurityPredicateId("dbo.u", 1), SecurityScope.Root)), null, null), null, null),
                    new SqlIdentifier("u", true)),
                null, null, null), null, null);

        Assert.Throws<SecurityCoverageException>(() => Verifier().Verify(host, CancellationToken.None));
    }
}
