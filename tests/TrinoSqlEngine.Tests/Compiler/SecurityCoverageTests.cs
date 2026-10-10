using System.Collections.Immutable;
using System.Reflection;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;
using static TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace TrinoSqlEngine.Tests.Compiler;

public class SecurityCoverageTests
{
    private static readonly SecurityPredicateId RootId = new("dbo.t", 0);
    private static readonly SecurityPredicateId TenantId = new("dbo.u", 0);

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(new[] { new SqlIdentifier(name, true) }));
    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);

    private static SqlQualifiedName Canonical(string table) =>
        new(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier(table, true) });

    private static Expression Pred(SecurityPredicateId id, SecurityScope scope, Expression? inner = null) =>
        new SecurityPredicateExpression(inner ?? new BinaryExpression(Col("tenant"), Equal, new PolicyParameterExpression("t", SqlParameterType.String)), id, scope);

    /// <summary>Secured derived table: (SELECT * FROM [dbo].[table] WHERE predicate) AS alias.</summary>
    private static SubqueryTableSource Secured(string table, string alias, Expression? where) =>
        new(new SelectStatement(null,
                new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                    new NamedTableSource(Canonical(table), null), where, null, null), null, null),
            new SqlIdentifier(alias, true));

    private static SelectStatement Select(TableSource from, Expression? where = null, WithClause? with = null) =>
        new(with, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, from, where, null, null), null, null);

    private static SelectStatement SelectBody(QueryBody body, WithClause? with = null) => new(with, body, null, null);

    private static QuerySpecification Spec(TableSource from, Expression? where = null) =>
        new(false, new SelectItem[] { new WildcardSelectItem(null) }, from, where, null, null);

    private static SecurityCoverageVerifier Verifier() => new(name =>
    {
        if (name.Parts.Count == 0) return null;
        return name.SimpleName.ToLowerInvariant() switch
        {
            "t" => new TableCoverageRequirement("dbo.t", "dbo", "t", ImmutableArray.Create(RootId), ImmutableArray.Create(new SecurityPredicateId("dbo.t", 0))),
            "u" => new TableCoverageRequirement("dbo.u", "dbo", "u", ImmutableArray.Create(TenantId), ImmutableArray.Create(TenantId)),
            _ => null
        };
    });

    private static void Verify(SqlStatement s) => Verifier().Verify(s, CancellationToken.None);

    [Fact]
    public void FullySecuredQuery_Passes_AndReportsAppliedPredicates()
    {
        var stmt = Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)));
        var applied = Verifier().Verify(stmt, CancellationToken.None);
        Assert.Equal(new[] { RootId }, applied);
    }

    [Fact]
    public void ValuesSource_NeedsNoPredicate_ExplicitlyRecognized()
    {
        Verify(SelectBody(new ValuesQueryBody(new[] { new RowValueExpression(new Expression[] { Int(1) }) })));
    }

    // One test per SecurityScope: the same table position without predicate must throw, with predicate must pass.
    public static IEnumerable<object[]> ScopeBuilders() => new[]
    {
        Case("Root", w => Select(Secured("t", "t", w))),
        Case("Subquery", w => Select(new SubqueryTableSource(Select(Secured("t", "t", w)), new SqlIdentifier("s", true)))),
        Case("CteBody", w => SelectBody(
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("c", true) }), null), null, null, null),
            new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("c", true), null, Select(Secured("t", "t", w))) }))),
        Case("SetOperationBranch", w => SelectBody(new SetOperationQuery(
            Spec(Secured("t", "a", Pred(RootId, SecurityScope.Root))), SetOperator.Union, false, Spec(Secured("t", "b", w))))),
        Case("Lateral", w => Select(new JoinedTableSource(
            Secured("t", "a", Pred(RootId, SecurityScope.Root)), JoinType.Cross,
            new LateralTableSource(Select(Secured("t", "b", w)), new SqlIdentifier("l", true)), null))),
        Case("ScalarSubquery", w => Select(Secured("t", "a", Pred(RootId, SecurityScope.Root)),
            new BinaryExpression(Col("x"), Equal, new ScalarSubqueryExpression(Select(Secured("t", "b", w)))))),
        Case("ExistsSubquery", w => Select(Secured("t", "a", Pred(RootId, SecurityScope.Root)),
            new ExistsExpression(Select(Secured("t", "b", w))))),
        Case("InSubquery", w => Select(Secured("t", "a", Pred(RootId, SecurityScope.Root)),
            new InSubqueryExpression(Col("x"), Select(Secured("t", "b", w)), false))),
    }.Select(c => new object[] { c.Name, c.Build });

    private static (string Name, Func<Expression?, SqlStatement> Build) Case(string name, Func<Expression?, SqlStatement> build) => (name, build);

    [Theory]
    [MemberData(nameof(ScopeBuilders))]
    public void Verifier_Throws_WhenScopeMissingPredicate(string scope, Func<Expression?, SqlStatement> build)
    {
        Assert.Throws<SecurityCoverageException>(() => Verify(build(null)));
        Assert.Throws<SecurityCoverageException>(() => Verify(build(new BinaryExpression(Col("x"), Equal, Int(1)))));
        Verify(build(Pred(RootId, SecurityScope.Root)));
        _ = scope;
    }

    [Fact]
    public void Verifier_Throws_WhenPredicateInJoinOnInsteadOfDerivedWhere()
    {
        var stmt = Select(new JoinedTableSource(
            Secured("t", "a", null), JoinType.Inner, Secured("t", "b", Pred(RootId, SecurityScope.Root)),
            new OnJoinCondition(Pred(RootId, SecurityScope.Root))));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void Verifier_Throws_WhenPredicateOnlyInOuterWhere()
    {
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "a", null), Pred(RootId, SecurityScope.Root))));
    }

    [Fact]
    public void Verifier_Throws_WhenPredicateIsNotATopLevelConjunct()
    {
        var hidden = new BinaryExpression(Pred(RootId, SecurityScope.Root), Or, new BinaryExpression(Col("x"), Equal, Int(1)));
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "a", hidden))));
        var conjunct = new BinaryExpression(new BinaryExpression(Col("x"), Equal, Int(1)), And, Pred(RootId, SecurityScope.Root));
        Verify(Select(Secured("t", "a", conjunct)));
    }

    [Fact]
    public void Verifier_Throws_ForPredicateOfAnotherTable()
    {
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "a", Pred(TenantId, SecurityScope.Root)))));
    }

    [Fact]
    public void Verifier_Throws_ForUnsecuredPhysicalTableInJoin()
    {
        var stmt = Select(new JoinedTableSource(
            Secured("t", "a", Pred(RootId, SecurityScope.Root)), JoinType.Inner,
            new NamedTableSource(Canonical("t"), null), null));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void Verifier_Throws_ForUnknownTable_UnresolvedNameIsNeverEmitted()
    {
        var stmt = Select(new SubqueryTableSource(
            Select(new NamedTableSource(Canonical("zzz"), null)), new SqlIdentifier("z", true)));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Theory]
    [InlineData("t", null)]                // unqualified
    [InlineData("T", "dbo")]               // case differs from the catalog canonical name
    public void Verifier_Throws_WhenPhysicalNameIsNotSchemaQualifiedCanonical(string table, string? schema)
    {
        var name = schema is null
            ? new SqlQualifiedName(new[] { new SqlIdentifier(table, true) })
            : new SqlQualifiedName(new[] { new SqlIdentifier(schema, true), new SqlIdentifier(table, true) });
        var stmt = Select(new SubqueryTableSource(
            SelectBody(Spec(new NamedTableSource(name, null), Pred(RootId, SecurityScope.Root))),
            new SqlIdentifier("a", true)));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void Verifier_Throws_WhenNameIsUnquoted()
    {
        var name = new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("t") });
        var stmt = Select(new SubqueryTableSource(
            SelectBody(Spec(new NamedTableSource(name, null), Pred(RootId, SecurityScope.Root))), new SqlIdentifier("a", true)));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void QuotedCteVsPhysical_CteNameNeverSuppressesPhysicalCheck()
    {
        // WITH t AS (SELECT * FROM <physical t, unsecured>) SELECT * FROM t -- the CTE named t must not hide the physical t.
        var unsecuredBody = Select(new NamedTableSource(Canonical("t"), null));
        var stmt = Select(new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null), null,
            new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("t", true), null, unsecuredBody) }));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));

        var securedBody = Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)));
        var ok = Select(new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("t", true) }), null), null,
            new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("t", true), null, securedBody) }));
        Verify(ok);
    }

    [Theory]
    [InlineData("t", false)]   // unquoted reference to a delimited definition: Oracle would bind it to the physical table T
    [InlineData("T", true)]    // different case: a case-sensitive database binds a different name
    [InlineData("t", true)]    // exact: the only accepted form
    public void CteReference_MustMatchTheDefinitionIdentifierExactly(string reference, bool quoted)
    {
        var body = Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)));
        var stmt = Select(new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier(reference, quoted) }), null), null,
            new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("t", true), null, body) }));

        if (quoted && reference == "t")
        {
            Verify(stmt);
        }
        else
        {
            Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
        }
    }

    [Fact]
    public void CteDefinition_MustBeDelimited()
    {
        var body = Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)));
        var stmt = Select(new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("c", true) }), null), null,
            new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("c"), null, body) }));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void RecursiveCteSelfReference_OnlyInsideWithRecursive()
    {
        SelectStatement Body(bool recursive) => Select(
            new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("r", true) }), null), null,
            new WithClause(recursive, new[]
            {
                new CommonTableExpression(new SqlIdentifier("r", true), null,
                    Select(new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("r", true) }), null)))
            }));

        Verify(Body(true));
        Assert.Throws<SecurityCoverageException>(() => Verify(Body(false)));
    }

    [Fact]
    public void PolicySubquery_WithoutTenantPredicate_Throws()
    {
        // The root predicate of t contains an EXISTS over u. Tables in policy subqueries need the tenant predicate of u.
        Expression Policy(Expression? uWhere) => Pred(RootId, SecurityScope.Root,
            new ExistsExpression(Select(Secured("u", "e", uWhere))));

        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "t", Policy(null)))));
        Verify(Select(Secured("t", "t", Policy(Pred(TenantId, SecurityScope.PolicySubquery)))));
    }

    [Fact]
    public void NestedPolicySubquery_IsRejected()
    {
        Expression Nested() => Pred(RootId, SecurityScope.Root, new ExistsExpression(Select(Secured("t", "x", Pred(RootId, SecurityScope.Root)))));
        var uWhere = new BinaryExpression(Pred(TenantId, SecurityScope.PolicySubquery), And, Nested());
        var policy = Pred(RootId, SecurityScope.Root, new ExistsExpression(Select(Secured("u", "e", uWhere))));
        Assert.Throws<SecurityCoverageException>(() => Verify(Select(Secured("t", "t", policy))));
    }

    [Fact]
    public void TrustedSqlExpression_OnTheTypedPath_IsRejected()
    {
        var stmt = Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)), new BinaryExpression(Col("x"), Equal, new TrustedSqlExpression("1")));
        Assert.Throws<SecurityCoverageException>(() => Verify(stmt));
    }

    [Fact]
    public void NonSelectStatements_AreRejected()
    {
        var del = new DeleteStatement(new NamedTableSource(Canonical("t"), null), null);
        Assert.Throws<SecurityCoverageException>(() => Verify(del));
    }

    [Fact]
    public void SecuredTableReferenceLimit_IsEnforced()
    {
        TableSource from = Secured("t", "a0", Pred(RootId, SecurityScope.Root));
        for (int i = 1; i <= 256; i++)
        {
            from = new JoinedTableSource(from, JoinType.Cross, Secured("t", "a" + i, Pred(RootId, SecurityScope.Root)), null);
        }

        var ex = Assert.Throws<SqlLimitExceededException>(() => Verify(Select(from)));
        Assert.Equal(SqlLimitKind.SecuredTableReferences, ex.Kind);
    }

    [Fact]
    public void Verifier_ObservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Verifier().Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root))), cts.Token));
    }

    [Fact]
    public void Verifier_DeepTree_ThrowsTypedLimit_NoCrash()
    {
        Expression deep = Int(1);
        for (int i = 0; i < 200_000; i++) deep = new BinaryExpression(deep, Or, Col("a"));
        var ex = Assert.Throws<SqlLimitExceededException>(() => Verify(Select(Secured("t", "t", Pred(RootId, SecurityScope.Root)), deep)));
        Assert.True(ex.Kind is SqlLimitKind.NestingDepth or SqlLimitKind.AstDepth);
    }

    // ---- opacity (INV-3) ----

    private static SelectStatement WithInjected(Expression injected, Expression? userWhere) =>
        Select(new NamedTableSource(Canonical("t"), null),
            userWhere is null ? injected : new BinaryExpression(userWhere, And, injected));

    [Fact]
    public void SimplifierRunsOnUserTree_InjectedDenyAllSurvives()
    {
        var deny = Pred(RootId, SecurityScope.Root, new BinaryExpression(Int(1), Equal, Int(0)));
        var user = new BinaryExpression(new BinaryExpression(Col("a"), Equal, Int(1)), And, new BinaryExpression(Col("a"), Equal, Int(1)));

        var result = (SelectStatement)new AstSimplificationVisitor().Visit(WithInjected(deny, user));

        var where = ((QuerySpecification)result.Body).Where!;
        Assert.True(Contains(where, deny), "injected predicate must survive simplification by reference");
    }

    [Fact]
    public void TautologyFolding_OfTheUserTree_DoesNotTouchInjectedPredicate()
    {
        var inner = new BinaryExpression(new LiteralExpression(true, LiteralType.Boolean), And, new BinaryExpression(Col("x"), Equal, Int(1)));
        var injected = Pred(RootId, SecurityScope.Root, inner);
        var result = (SelectStatement)new AstSimplificationVisitor().Visit(WithInjected(injected, new BinaryExpression(new BinaryExpression(Col("a"), Equal, Int(1)), Or, new LiteralExpression(true, LiteralType.Boolean))));
        var sp = FindSecurityPredicate(((QuerySpecification)result.Body).Where!);
        Assert.Same(injected, sp); // not even the TRUE conjunct inside is rewritten
    }

    [Fact]
    public void NoRewriterDescendsIntoSecurityPredicate()
    {
        var inner = new UnaryExpression(UnaryOperator.Not, new UnaryExpression(UnaryOperator.Not, new BinaryExpression(Int(1), Equal, Int(1))));
        var injected = Pred(RootId, SecurityScope.Root, inner);
        var stmt = WithInjected(injected, new BinaryExpression(Col("a"), Equal, Int(1)));

        var rewriterTypes = typeof(SqlAstRewriter).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(SqlAstRewriter).IsAssignableFrom(t))
            .ToList();
        Assert.Contains(typeof(AstSimplificationVisitor), rewriterTypes);
        Assert.Contains(typeof(AstValidationVisitor), rewriterTypes);

        foreach (var type in rewriterTypes)
        {
            if (type == typeof(AstSecurityVisitor)) continue; // creates the nodes; never re-enters them (tested below)
            // Producers that run before an expression is wrapped in a SecurityPredicateExpression need constructor arguments.
            if (type.Name is "PolicySubqueryTenantRewriter" or "PolicyShaper") continue;
            var rewriter = (SqlAstRewriter)Activator.CreateInstance(type, BindingFlags.Default | BindingFlags.OptionalParamBinding, null, Array.Empty<object>(), null)!;
            var result = rewriter.Visit(stmt);
            Assert.True(ContainsStatementPredicate(result, injected), $"{type.Name} rewrote an injected security predicate");
        }
    }

    [Fact]
    public void SecurityVisitor_DoesNotReenterAnInjectedPredicate()
    {
        var injected = Pred(RootId, SecurityScope.Root, new ExistsExpression(Select(new NamedTableSource(Canonical("zzz"), null))));
        var visitor = new AstSecurityVisitor(new RlsOptions { PolicyProvider = new DefaultRlsPolicyProvider("1 = 1") });
        var result = (SelectStatement)visitor.Visit(Select(new SubqueryTableSource(Select(Secured("t", "x", null)), new SqlIdentifier("a", true)), injected));
        Assert.Same(injected, FindSecurityPredicate(((QuerySpecification)result.Body).Where!));
    }

    private static bool Contains(Expression where, Expression target) => FindSecurityPredicate(where) is { } sp && ReferenceEquals(sp, target);

    private static bool ContainsStatementPredicate(SqlNode result, Expression injected) =>
        result is SelectStatement { Body: QuerySpecification { Where: { } w } } && Contains(w, injected);

    private static SecurityPredicateExpression? FindSecurityPredicate(Expression e) => e switch
    {
        SecurityPredicateExpression sp => sp,
        BinaryExpression b => FindSecurityPredicate(b.Left) ?? FindSecurityPredicate(b.Right),
        ParenthesizedExpression p => FindSecurityPredicate(p.Expression),
        _ => null
    };

    [Fact]
    public void SecurityPredicate_IsEmittedParenthesized()
    {
        var inner = new BinaryExpression(new BinaryExpression(Col("a"), Equal, Int(1)), Or, new BinaryExpression(Col("b"), Equal, Int(2)));
        var stmt = Select(new NamedTableSource(Canonical("t"), null),
            new BinaryExpression(new BinaryExpression(Col("x"), Equal, Int(3)), And, Pred(RootId, SecurityScope.Root, inner)));
        var sql = CompilerTestHelpers.GenerateSqlServer(stmt).Sql;
        Assert.Contains("AND ([a] = @p1 OR [b] = @p2)", sql);
    }

    [Fact]
    public void SystemQueryPassthrough_IsRejected_ByTheBuilder()
    {
        Assert.ThrowsAny<Exception>(() => CompilerTestHelpers.Build("SELECT * FROM TABLE(system.query(query => 'SELECT 1'))"));
    }
}
