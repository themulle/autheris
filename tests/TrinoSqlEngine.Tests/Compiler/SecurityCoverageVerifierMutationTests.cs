using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using Xunit;
using static TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for <see cref="SecurityCoverageVerifier"/>: the verifier never trusts the injector, so it must reach every
/// expression position that can hold a subquery, reject with the typed reason, and enforce its depth and stack limits.
/// </summary>
[Trait("Category", "SqlGate")]
public class SecurityCoverageVerifierMutationTests
{
    private static readonly SecurityPredicateId RootId = new("dbo.t", 0);
    private static readonly SecurityPredicateId TenantId = new("dbo.u", 0);

    private sealed record UnknownExpression : Expression;

    private sealed record UnknownSource : TableSource;

    private sealed record UnknownStatement : SqlStatement;

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));

    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);

    private static SqlQualifiedName Canonical(string table) => new(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier(table, true) });

    private static Expression Pred(SecurityPredicateId id, SecurityScope scope, Expression? inner = null) =>
        new SecurityPredicateExpression(inner ?? new BinaryExpression(Col("tenant"), Equal, new PolicyParameterExpression("t", SqlParameterType.String)), id, scope);

    private static SubqueryTableSource Secured(string table, string alias, Expression? where) =>
        new(new SelectStatement(null,
                new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) },
                    new NamedTableSource(Canonical(table), null), where, null, null), null, null),
            new SqlIdentifier(alias, true));

    private static SelectStatement Select(TableSource from, Expression? where = null) =>
        new(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, from, where, null, null), null, null);

    private static SubqueryTableSource SecuredT() => Secured("t", "t", Pred(RootId, SecurityScope.Root));

    private static SecurityCoverageVerifier Verifier(int maxAstDepth = 512) => new(name =>
    {
        if (name.Parts.Count == 0) return null;
        return name.SimpleName.ToLowerInvariant() switch
        {
            "t" => new TableCoverageRequirement("dbo.t", "dbo", "t", ImmutableArray.Create(RootId), ImmutableArray.Create(new SecurityPredicateId("dbo.t", 0)),
                MaskedColumns: ImmutableHashSet.Create("secret"), CatalogColumns: ImmutableHashSet.Create("id", "secret")),
            "u" => new TableCoverageRequirement("dbo.u", "dbo", "u", ImmutableArray.Create(TenantId), ImmutableArray.Create(TenantId), CatalogColumns: ImmutableHashSet.Create("id")),
            _ => null
        };
    }, maxAstDepth: maxAstDepth);

    private static void Verify(SqlStatement s, int maxAstDepth = 512) => Verifier(maxAstDepth).Verify(s, CancellationToken.None);

    private static void AssertRejected(SqlStatement s, string message) =>
        Assert.Equal(message, Assert.Throws<SecurityCoverageException>(() => Verify(s)).Message);

    /// <summary>A subquery over a physical table that is not secured: the verifier must reject it wherever it appears.</summary>
    private static Expression Bad() => new ExistsExpression(Select(new NamedTableSource(Canonical("t"), null)));

    private static Expression Ok() => Int(1);

    private static SelectStatement Root(Expression? where = null, GroupByClause? groupBy = null, Expression? having = null,
        OrderByClause? orderBy = null, PaginationClause? pagination = null, IReadOnlyList<SelectItem>? projections = null) =>
        new(null, new QuerySpecification(false, projections ?? new SelectItem[] { new WildcardSelectItem(null) }, SecuredT(), where, groupBy, having), orderBy, pagination);

    // ---- every position that can hold a subquery is traversed ----

    public static IEnumerable<object[]> Positions()
    {
        var cases = new List<(string Name, Func<Expression, SqlStatement> Build)>
        {
            ("select-item", e => Root(projections: new SelectItem[] { new ColumnSelectItem(e, null) })),
            ("where", e => Root(where: e)),
            ("having", e => Root(having: e)),
            ("group-by", e => Root(groupBy: new GroupByClause(new[] { e }))),
            ("group-by-grouping-sets", e => Root(groupBy: new GroupByClause(Array.Empty<Expression>(),
                new[] { new GroupingElement(GroupingElementKind.GroupingSets, new IReadOnlyList<Expression>[] { new[] { e } }) }))),
            ("order-by", e => Root(orderBy: new OrderByClause(new[] { new OrderByElement(e) }))),
            ("offset", e => Root(pagination: new PaginationClause(e, null))),
            ("limit", e => Root(pagination: new PaginationClause(null, e))),
            ("join-on", e => Select(new JoinedTableSource(SecuredT(), JoinType.Inner, Secured("u", "u", Pred(TenantId, SecurityScope.Root)), new OnJoinCondition(e)))),
            ("parenthesized", e => Root(new ParenthesizedExpression(e))),
            ("binary-left", e => Root(new BinaryExpression(e, Equal, Ok()))),
            ("binary-right", e => Root(new BinaryExpression(Ok(), Equal, e))),
            ("unary", e => Root(new UnaryExpression(UnaryOperator.Not, e))),
            ("like-operand", e => Root(new LikeExpression(e, Ok()))),
            ("like-pattern", e => Root(new LikeExpression(Ok(), e))),
            ("like-escape", e => Root(new LikeExpression(Ok(), Ok(), e))),
            ("in-list-operand", e => Root(new InListExpression(e, new[] { Ok() }, false))),
            ("in-list-item", e => Root(new InListExpression(Ok(), new[] { Ok(), e }, false))),
            ("in-subquery-operand", e => Root(new InSubqueryExpression(e, Select(SecuredT()), false))),
            ("quantified-left", e => Root(new QuantifiedComparisonExpression(e, Equal, ComparisonQuantifier.Any, Select(SecuredT())))),
            ("quantified-subquery", e => Root(new QuantifiedComparisonExpression(Ok(), Equal, ComparisonQuantifier.Any, Select(SecuredT(), e)))),
            ("is-distinct-left", e => Root(new IsDistinctFromExpression(e, Ok(), false))),
            ("is-distinct-right", e => Root(new IsDistinctFromExpression(Ok(), e, false))),
            ("between-operand", e => Root(new BetweenExpression(e, Ok(), Ok(), false))),
            ("between-lower", e => Root(new BetweenExpression(Ok(), e, Ok(), false))),
            ("between-upper", e => Root(new BetweenExpression(Ok(), Ok(), e, false))),
            ("case-operand", e => Root(new CaseExpression(e, new[] { new WhenClause(Ok(), Ok()) }, null))),
            ("case-when-condition", e => Root(new CaseExpression(null, new[] { new WhenClause(e, Ok()) }, null))),
            ("case-when-result", e => Root(new CaseExpression(null, new[] { new WhenClause(Ok(), e) }, null))),
            ("case-else", e => Root(new CaseExpression(null, new[] { new WhenClause(Ok(), Ok()) }, e))),
            ("function-argument", e => Root(new FunctionCallExpression(new SqlQualifiedName("f"), new[] { e }))),
            ("function-filter", e => Root(new FunctionCallExpression(new SqlQualifiedName("f"), new[] { Ok() }, Filter: e))),
            ("function-order-within", e => Root(new FunctionCallExpression(new SqlQualifiedName("f"), new[] { Ok() },
                OrderWithin: new OrderByClause(new[] { new OrderByElement(e) })))),
            ("window-partition", e => Root(new FunctionCallExpression(new SqlQualifiedName("f"), new[] { Ok() },
                Window: new WindowSpecification(new[] { e }, null)))),
            ("window-order", e => Root(new FunctionCallExpression(new SqlQualifiedName("f"), new[] { Ok() },
                Window: new WindowSpecification(null, new OrderByClause(new[] { new OrderByElement(e) }))))),
            ("cast", e => Root(new CastExpression(e, "int"))),
            ("row", e => Root(new RowValueExpression(new[] { Ok(), e }))),
            ("array", e => Root(new ArrayConstructorExpression(new[] { Ok(), e }))),
            ("subscript-target", e => Root(new SubscriptExpression(e, Ok()))),
            ("subscript-index", e => Root(new SubscriptExpression(Ok(), e))),
            ("extract", e => Root(new ExtractExpression("year", e))),
            ("substring-source", e => Root(new SubstringExpression(e, Ok(), null))),
            ("substring-start", e => Root(new SubstringExpression(Ok(), e, null))),
            ("substring-length", e => Root(new SubstringExpression(Ok(), Ok(), e))),
            ("trim-source", e => Root(new TrimExpression(TrimSpecification.Both, e, null))),
            ("trim-characters", e => Root(new TrimExpression(TrimSpecification.Both, Ok(), e))),
            ("position-needle", e => Root(new PositionExpression(e, Ok()))),
            ("position-haystack", e => Root(new PositionExpression(Ok(), e))),
            ("date-function", e => Root(new DateFunctionExpression(DateFunctionKind.Add, DateUnit.Day, 1, e))),
        };

        return cases.Select(c => new object[] { c.Name, c.Build });
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void UnsecuredTableInAnyPosition_IsRejected_WhileABenignExpressionPasses(string position, Func<Expression, SqlStatement> build)
    {
        _ = position;
        Verify(build(Ok()));
        Assert.Throws<SecurityCoverageException>(() => Verify(build(Bad())));
    }

    [Fact]
    public void GroupingOperation_ColumnsAreVisited_AndEmptyAdvancedElementsAreAccepted()
    {
        Verify(Root(groupBy: new GroupByClause(Array.Empty<Expression>(), null)));
        Verify(Root(projections: new SelectItem[] { new ColumnSelectItem(new GroupingOperationExpression(new[] { Col("a") }), null) }));
    }

    // ---- typed reasons ----

    [Fact]
    public void UnsupportedStatement_IsRejected_WithItsTypeName()
    {
        AssertRejected(new UnknownStatement(), "Statement 'UnknownStatement' cannot be verified on the governed compiler path.");
    }

    [Fact]
    public void UnknownExpressionNode_FailsClosed_WithItsTypeName()
    {
        AssertRejected(Root(new UnknownExpression()), "Expression node 'UnknownExpression' is not supported by the coverage verifier.");
    }

    [Fact]
    public void UnknownTableSource_FailsClosed_WithItsTypeName()
    {
        AssertRejected(Select(new JoinedTableSource(SecuredT(), JoinType.Cross, new UnknownSource(), null)), "Table source 'UnknownSource' is not supported.");
    }

    [Fact]
    public void TableQueryBody_IsNeverEmitted_AndRejectedWithItsTypeName()
    {
        var stmt = new SelectStatement(null, new TableQueryBody(Canonical("t")), null, null);
        AssertRejected(stmt, "Query body 'TableQueryBody' is not covered by a security predicate.");
    }

    [Fact]
    public void UndelimitedCteName_IsRejected()
    {
        var cte = new CommonTableExpression(new SqlIdentifier("c", false), null, Select(SecuredT()));
        var stmt = new SelectStatement(new WithClause(false, new[] { cte }), new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("c", true) }), null), null, null, null), null, null);
        AssertRejected(stmt, "A CTE name is not emitted as a delimited identifier.");
    }

    [Fact]
    public void PhysicalTableOutsideASecuredDerivedTable_IsRejected_WithTheTypedReason()
    {
        var stmt = Select(new JoinedTableSource(SecuredT(), JoinType.Cross, new NamedTableSource(Canonical("t"), null), null));
        AssertRejected(stmt, "A physical table is referenced outside a secured derived table.");
    }

    [Fact]
    public void UnresolvableTable_IsRejected_WithTheTypedReason()
    {
        AssertRejected(Select(new NamedTableSource(Canonical("ghost"), null)), "A table reference could not be resolved against the catalog.");
    }

    [Fact]
    public void NonCanonicalPhysicalName_IsRejected_WithTheTypedReason()
    {
        var name = new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("T", true) });
        var inner = new SelectStatement(null, new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(Col("id"), new SqlIdentifier("id", true)) },
            new NamedTableSource(name, null), Pred(RootId, SecurityScope.Root), null, null), null, null);
        AssertRejected(Select(new SubqueryTableSource(inner, new SqlIdentifier("t", true))), "A physical table is not emitted as its schema-qualified canonical name.");
    }

    [Fact]
    public void RawLiteralInAnInjectedPredicate_IsRejected_ButNullAndBooleanAreKeywords()
    {
        const string message = "An injected security predicate contains a raw literal value.";
        AssertRejected(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Col("a"), Equal, Int(5))))), message);
        AssertRejected(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Col("a"), Equal, new LiteralExpression("x", LiteralType.String))))), message);
        Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Col("a"), Equal, new LiteralExpression(null, LiteralType.Null))))));
        Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Col("a"), Equal, new LiteralExpression(true, LiteralType.Boolean))))));
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("interval")]
    [InlineData("date-function")]
    public void TypedLiteralsInAnInjectedPredicate_AreRejected_ButAllowedInUserExpressions(string kind)
    {
        Expression Literal() => kind switch
        {
            "typed" => new TypedLiteralExpression(TypedLiteralKind.Date, "2026-01-01"),
            "interval" => new IntervalLiteralExpression("1", "day"),
            _ => new DateFunctionExpression(DateFunctionKind.Add, DateUnit.Day, 1, Col("d"))
        };

        AssertRejected(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Col("a"), Equal, Literal())))),
            "An injected security predicate contains a raw literal value.");
        Verify(Root(new BinaryExpression(Col("a"), Equal, Literal())));
    }

    [Fact]
    public void IntegerLiteralOutsideAPredicate_IsAllowed()
    {
        Verify(Root(new BinaryExpression(Col("a"), Equal, Int(5))));
    }

    [Fact]
    public void CanonicalConstantsInAPredicate_AreAllowed_ButOtherIntegerComparisonsAreNot()
    {
        Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(1), Equal, Int(1))))));
        Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(1), Equal, Int(0))))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(2), Equal, Int(2)))))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(1), Equal, Int(2)))))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(1), NotEqual, Int(1)))))));
    }

    // ---- policy subqueries are one level deep ----

    public static IEnumerable<object[]> SelectContainers()
    {
        static Expression Sub() => new ExistsExpression(Select(Secured("t", "x", Pred(RootId, SecurityScope.Root))));
        var cases = new List<(string, Func<Expression>)>
        {
            ("exists", () => Sub()),
            ("in-subquery", () => new InSubqueryExpression(Col("a"), Select(Secured("t", "x", Pred(RootId, SecurityScope.Root))), false)),
            ("scalar-subquery", () => new BinaryExpression(Col("a"), Equal, new ScalarSubqueryExpression(Select(Secured("t", "x", Pred(RootId, SecurityScope.Root)))))),
            ("quantified", () => new QuantifiedComparisonExpression(Col("a"), Equal, ComparisonQuantifier.Any, Select(Secured("t", "x", Pred(RootId, SecurityScope.Root))))),
            ("binary-left", () => new BinaryExpression(Sub(), Or, Int(1))),
            ("binary-right", () => new BinaryExpression(Int(1), Or, Sub())),
            ("parenthesized", () => new ParenthesizedExpression(Sub())),
            ("unary", () => new UnaryExpression(UnaryOperator.Not, Sub())),
        };
        return cases.Select(c => new object[] { c.Item1, c.Item2 });
    }

    [Theory]
    [MemberData(nameof(SelectContainers))]
    public void PolicySubqueryPredicate_ContainingASelect_IsRejectedAsNested(string shape, Func<Expression> nested)
    {
        _ = shape;
        // the predicate carries the policy-subquery scope, but it hides a further SELECT: policy subqueries are one level deep
        var uWhere = new BinaryExpression(Pred(TenantId, SecurityScope.PolicySubquery, nested()), And, Pred(TenantId, SecurityScope.PolicySubquery));
        var policy = Pred(RootId, SecurityScope.Root, new ExistsExpression(Select(Secured("u", "e", uWhere))));

        AssertRejected(Select(Secured("t", "t", policy)), "Nested policy subqueries are not permitted.");
    }

    [Fact]
    public void PolicySubqueryPredicate_OfAnotherScope_IsRejectedAsNested()
    {
        var uWhere = Pred(TenantId, SecurityScope.Root);
        var policy = Pred(RootId, SecurityScope.Root, new ExistsExpression(Select(Secured("u", "e", uWhere))));

        AssertRejected(Select(Secured("t", "t", policy)), "Nested policy subqueries are not permitted.");
    }

    // ---- limits ----

    [Fact]
    public void AstDepthLimit_IsEnforced_WithTheMeasuredDepth()
    {
        var stmt = Root(new ParenthesizedExpression(new ParenthesizedExpression(new ParenthesizedExpression(Ok()))));

        var ex = Assert.Throws<SqlLimitExceededException>(() => Verify(stmt, maxAstDepth: 3));

        Assert.Equal(SqlLimitKind.AstDepth, ex.Kind);
        Assert.Equal(3, ex.Maximum);
        Assert.Equal(4, ex.Requested);
    }

    [Fact]
    public void StackExhaustion_IsReportedAsTheNestingDepthLimit()
    {
        using var _ = CompilerHooks.Use(new MutantHooks { ExhaustStackAtGuardCall = 1 });

        var ex = Assert.Throws<SqlLimitExceededException>(() => Verify(Root(Ok())));

        Assert.Equal(SqlLimitKind.NestingDepth, ex.Kind);
    }
}
