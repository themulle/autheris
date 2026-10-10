using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

public class PolicyExpressionParserTests
{
    private readonly FakeTimeProvider _time = new();
    private PolicyExpressionParser NewParser(PolicyParseCacheOptions? options = null) => new(new FastSqlEngine(), _time, options);

    private static PolicyParseContext Ctx(Action<PolicyContextBuilder>? configure = null)
    {
        var b = Context(Orders);
        configure?.Invoke(b);
        return b.Build();
    }

    private static IEnumerable<T> Walk<T>(object? node) where T : class
    {
        if (node is null || node is string) yield break;
        if (node is T match) yield return match;
        if (node is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
                foreach (var m in Walk<T>(item)) yield return m;
            yield break;
        }

        if (node is SqlNode)
        {
            foreach (var p in node.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType.IsEnum) continue;
                foreach (var m in Walk<T>(p.GetValue(node))) yield return m;
            }
        }
    }

    [Fact]
    public void CanonicalPredicate_ParsesAndEveryValueBecomesAPolicyParameter()
    {
        var p = NewParser().Parse("region = 'EU' AND status IN ('a', 'b') AND amount > 10", Ctx());

        Assert.Empty(Walk<LiteralExpression>(p.Expression));
        var parameters = Walk<PolicyParameterExpression>(p.Expression).ToList();
        Assert.Equal(4, parameters.Count);
        Assert.Equal(4, p.Parameters.Count);
        Assert.Contains(p.Parameters.Values, v => Equals(v.Value, "EU") && v.Type == SqlParameterType.String);
        Assert.Contains(p.Parameters.Values, v => v.Type == SqlParameterType.Int32 && Equals(v.Value, 10));
        Assert.All(parameters, x => Assert.Equal(ParameterOrigin.Policy, x.Origin));
    }

    [Fact]
    public void Columns_AreResolvedToCatalogCanonicalQuotedNames()
    {
        var p = NewParser().Parse("REGION = 'EU'", Ctx());
        var col = Assert.Single(Walk<ColumnReference>(p.Expression));
        var id = Assert.Single(col.Name.Parts);
        Assert.Equal("Region", id.Value);
        Assert.True(id.IsQuoted);
    }

    [Fact]
    public void ReferencedColumns_AreExposed()
    {
        var p = NewParser().Parse("region = 'EU' AND status = 'x'", Ctx());
        Assert.Contains("region", p.ReferencedColumns);
        Assert.Contains("status", p.ReferencedColumns);
        Assert.DoesNotContain("amount", p.ReferencedColumns);
    }

    [Theory]
    [InlineData("nosuchcolumn = 'x'")]
    [InlineData("other.region = 'x'")]
    [InlineData("lower(region) = 'x'")]                       // function not on the allow-list
    [InlineData("region = 'EU' -- tail")]
    [InlineData("region = 'EU' /* c */")]
    [InlineData("region = 'EU'; DROP TABLE x")]
    [InlineData("region = ?")]
    [InlineData("region = :undeclared")]
    [InlineData("region = 'a\\b'")]
    [InlineData("")]
    [InlineData("region =")]
    public void InvalidPredicates_AreRejected_FailClosed(string text)
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse(text, Ctx()));
    }

    [Fact]
    public void PolicyParseException_Messages_DoNotEchoPolicyText()
    {
        var ex = Assert.Throws<PolicyParseException>(() => NewParser().Parse("secretcolumn_xyz = 'hunter2-value'", Ctx()));
        Assert.DoesNotContain("hunter2-value", ex.Message);
    }

    [Fact]
    public void AllowedFunction_Parses()
    {
        var p = NewParser().Parse("lower(region) = 'eu'", Ctx(b => b.Functions.Add("lower")));
        Assert.Single(Walk<FunctionCallExpression>(p.Expression));
    }

    [Theory]
    [InlineData("region = current_user")]
    [InlineData("random() < 0.5")]
    [InlineData("rand() < 0.5")]
    [InlineData("now() > amount")]
    [InlineData("uuid() = region")]
    [InlineData("newid() = region")]
    [InlineData("getdate() > amount")]
    [InlineData("session_user = region")]
    [InlineData("current_timestamp > amount")]
    [InlineData("current_date > amount")]
    public void NondeterministicAndSessionFunctions_AreRejected_EvenWhenAllowListed(string text)
    {
        var ctx = Ctx(b => { foreach (var f in new[] { "random", "rand", "now", "uuid", "newid", "getdate", "current_user", "session_user" }) b.Functions.Add(f); });
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse(text, ctx));
    }

    [Fact]
    public void WindowFunctions_AreRejected()
    {
        var ctx = Ctx(b => { b.Functions.Add("row_number"); b.Functions.Add("sum"); });
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse("row_number() OVER (ORDER BY id) = 1", ctx));
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse("sum(amount) OVER (PARTITION BY region) > 1", ctx));
    }

    [Fact]
    public void PolicyParser_MaxNodes256()
    {
        string ok = string.Join(" OR ", Enumerable.Range(0, 40).Select(_ => "region = 'EU'"));
        NewParser().Parse(ok, Ctx());
        string tooBig = string.Join(" OR ", Enumerable.Range(0, 200).Select(_ => "region = 'EU'"));
        Assert.Throws<PolicyParseException>(() => NewParser().Parse(tooBig, Ctx()));
    }

    [Fact]
    public void NamedMarker_BecomesPolicyParameter_WithDeclaredTypeAndValue()
    {
        var p = NewParser().Parse("region = :r AND amount >= :min",
            Ctx(b =>
            {
                b.Parameters["r"] = new PolicyValue("EU", SqlParameterType.String);
                b.Parameters["min"] = new PolicyValue(5, SqlParameterType.Int32);
            }));
        Assert.Contains(Walk<PolicyParameterExpression>(p.Expression), x => x.Name == "r" && x.Type == SqlParameterType.String);
        Assert.Equal("EU", p.Parameters["r"].Value);
        Assert.Equal(5, p.Parameters["min"].Value);
    }

    [Fact]
    public void LikePatternLiteral_IsMarkedAsIntentionalPattern()
    {
        var p = NewParser().Parse("email LIKE '%@corp.example'", Ctx());
        var like = Assert.Single(Walk<LikeExpression>(p.Expression));
        var pattern = Assert.IsType<PolicyParameterExpression>(like.Pattern);
        Assert.True(pattern.IsLikePattern);
    }

    [Fact]
    public void LikeWithExplicitEscape_IsRejected()
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse("email LIKE 'a!%' ESCAPE '!'", Ctx()));
    }

    // ---- subqueries (SEC-ADG-11) ----

    private static readonly string Correlated =
        "EXISTS (SELECT 1 FROM entitlements e WHERE e.orderid = autheris_target.id)";

    [Fact]
    public void CorrelatedSubquery_AgainstCatalogTable_IsAllowed_WithCanonicalName()
    {
        var p = NewParser().Parse(Correlated, Ctx(b => b.Catalog = Catalog()));
        var table = Assert.Single(Walk<NamedTableSource>(p.Expression));
        Assert.Equal(new[] { "dbo", "Entitlements" }, table.Name.Parts.Select(x => x.Value));
        Assert.All(table.Name.Parts, x => Assert.True(x.IsQuoted));
    }

    [Fact]
    public void Subquery_WithoutCatalog_IsRejected()
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() => NewParser().Parse(Correlated, Ctx()));
    }

    [Fact]
    public void Subquery_AgainstNonCatalogTable_IsRejected()
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() =>
            NewParser().Parse("EXISTS (SELECT 1 FROM secrets s WHERE s.id = autheris_target.id)", Ctx(b => b.Catalog = Catalog())));
    }

    [Fact]
    public void PolicyParser_NestedPolicySubquery_Rejected()
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() =>
            NewParser().Parse(
                "EXISTS (SELECT 1 FROM entitlements e WHERE e.orderid = autheris_target.id AND EXISTS (SELECT 1 FROM lookup l WHERE l.code = 'x'))",
                Ctx(b => b.Catalog = Catalog())));
    }

    // ---- cache (SEC-ADG-20) ----

    [Fact]
    public void Cache_HitsOnSameTextAndCatalogVersion_AndRebindsValues()
    {
        var parser = NewParser();
        var first = parser.Parse("region = :r", Ctx(b => b.Parameters["r"] = new PolicyValue("EU", SqlParameterType.String)));
        var second = parser.Parse("region = :r", Ctx(b => b.Parameters["r"] = new PolicyValue("US", SqlParameterType.String)));

        Assert.Equal(1, parser.CacheStats.Hits);
        Assert.Equal("EU", first.Parameters["r"].Value);
        Assert.Equal("US", second.Parameters["r"].Value);   // cached shape, current values
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Cache_InvalidatesOnCatalogVersion()
    {
        var parser = NewParser();
        parser.Parse("region = 'EU'", Ctx());
        parser.Parse("region = 'EU'", Ctx(b => b.Version = 2));
        Assert.Equal(0, parser.CacheStats.Hits);
        Assert.Equal(2, parser.CacheStats.Misses);
    }

    [Fact]
    public void PolicyParseCache_KeyIncludesFunctionAllowListAndCompilerVersion()
    {
        var a = Ctx(b => b.Functions.Add("lower"));
        var b2 = Ctx(b => b.Functions.Add("upper"));
        Assert.NotEqual(PolicyExpressionParser.ComputeCacheKey("x", a, "v1"), PolicyExpressionParser.ComputeCacheKey("x", b2, "v1"));
        Assert.NotEqual(PolicyExpressionParser.ComputeCacheKey("x", a, "v1"), PolicyExpressionParser.ComputeCacheKey("x", a, "v2"));
        Assert.NotEqual(
            PolicyExpressionParser.ComputeCacheKey("x", Ctx(c => c.Parameters["p"] = new PolicyValue("a", SqlParameterType.String)), "v1"),
            PolicyExpressionParser.ComputeCacheKey("x", Ctx(c => c.Parameters["p"] = new PolicyValue(1, SqlParameterType.Int32)), "v1"));
        // values are not part of the key
        Assert.Equal(
            PolicyExpressionParser.ComputeCacheKey("x", Ctx(c => c.Parameters["p"] = new PolicyValue("a", SqlParameterType.String)), "v1"),
            PolicyExpressionParser.ComputeCacheKey("x", Ctx(c => c.Parameters["p"] = new PolicyValue("b", SqlParameterType.String)), "v1"));
    }

    [Fact]
    public void PolicyParseCache_NegativeEntryHasShortTtl()
    {
        var parser = NewParser(new PolicyParseCacheOptions { NegativeTtl = TimeSpan.FromSeconds(5) });
        Assert.Throws<PolicyParseException>(() => parser.Parse("nosuchcolumn = 1", Ctx()));
        Assert.Throws<PolicyParseException>(() => parser.Parse("nosuchcolumn = 1", Ctx()));
        Assert.Equal(1, parser.CacheStats.NegativeHits);

        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Throws<PolicyParseException>(() => parser.Parse("nosuchcolumn = 1", Ctx()));
        Assert.Equal(1, parser.CacheStats.NegativeHits);   // expired: parsed again
    }

    [Fact]
    public void PolicyParseCache_OnePartitionCannotEvictAnother()
    {
        var parser = NewParser(new PolicyParseCacheOptions { MaxEntries = 100, MaxEntriesPerPartition = 5 });
        parser.Parse("region = 'keep'", Ctx(b => b.Partition = "tenantB"));
        for (int i = 0; i < 50; i++)
        {
            parser.Parse($"status = 'v{i}'", Ctx(b => b.Partition = "tenantA"));
        }

        long hitsBefore = parser.CacheStats.Hits;
        parser.Parse("region = 'keep'", Ctx(b => b.Partition = "tenantB"));
        Assert.Equal(hitsBefore + 1, parser.CacheStats.Hits);
        Assert.True(parser.CacheStats.Entries <= 100);
    }

    // ---- PolicyPredicate ----

    [Fact]
    public void PolicyPredicate_And_ConflictingParameter_Throws()
    {
        var a = PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName("a")), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["p"] = new("x", SqlParameterType.String) });
        var b = PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName("b")), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["p"] = new("y", SqlParameterType.String) });

        Assert.Throws<PolicyConflictException>(() => a.And(b));
    }

    [Fact]
    public void PolicyPredicate_And_MergesParametersAndFingerprint()
    {
        var a = PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName("a")), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["p"] = new("x", SqlParameterType.String) });
        var b = PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName("b")), BinaryOperator.Equal, new PolicyParameterExpression("q", SqlParameterType.Int32)),
            new Dictionary<string, PolicyValue> { ["q"] = new(1, SqlParameterType.Int32) });

        var both = a.And(b);

        Assert.Equal(2, both.Parameters.Count);
        Assert.NotEqual(a.Fingerprint, both.Fingerprint);
        Assert.Contains("a", both.ReferencedColumns);
        Assert.Contains("b", both.ReferencedColumns);
        // same name and same value is not a conflict
        Assert.Single(a.And(a).Parameters);
    }

    [Fact]
    public void PolicyPredicate_Fingerprint_IgnoresValues_ButNotShape()
    {
        PolicyPredicate Make(string col, object value) => PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName(col)), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["p"] = new(value, SqlParameterType.String) });

        Assert.Equal(Make("a", "x").Fingerprint, Make("a", "y").Fingerprint);
        Assert.NotEqual(Make("a", "x").Fingerprint, Make("b", "x").Fingerprint);
        Assert.Equal(64, Make("a", "x").Fingerprint.Length); // SHA-256 hex
    }

    [Fact]
    public void PolicyPredicate_Create_WithMissingValue_Throws()
    {
        Assert.Throws<PolicyConflictException>(() => PolicyPredicate.Create(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName("a")), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String)),
            new Dictionary<string, PolicyValue>()));
    }

    [Fact]
    public void DenyAll_IsOneEqualsZero_NoParameters()
    {
        Assert.Empty(PolicyPredicate.DenyAll.Parameters);
        var eq = Assert.IsType<BinaryExpression>(PolicyPredicate.DenyAll.Expression);
        Assert.Equal(BinaryOperator.Equal, eq.Operator);
        Assert.Same(PolicyPredicate.DenyAll, PolicyPredicate.DenyAll);
    }

    // ---- hostile values (INV-5) ----

    [Theory]
    [InlineData("'; DROP TABLE t; --")]
    [InlineData("a' OR '1'='1")]
    [InlineData("]; --")]
    [InlineData("${x} \\ /* $$ E'x' q'[x]'")]
    public void HostilePolicyValues_AreBound_NeverInlined(string hostile)
    {
        var p = NewParser().Parse("region = :r", Ctx(b => b.Parameters["r"] = new PolicyValue(hostile, SqlParameterType.String)));
        var stmt = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Orders", true) }), null),
                p.Expression, null, null), null, null);

        var compiled = CompilerTestHelpers.GenerateSqlServer(stmt, p.Parameters);

        Assert.DoesNotContain('\'', compiled.Sql);
        Assert.DoesNotContain("DROP", compiled.Sql);
        Assert.Equal(hostile, Assert.Single(compiled.Parameters).Value);
    }

    // ---- LIKE escaping (SEC-ADG-18) ----

    [Fact]
    public void PolicyLike_EmitsExplicitEscape_AndEscapesStructuredValue()
    {
        var like = new LikeExpression(
            new ColumnReference(new SqlQualifiedName("n")),
            new PolicyParameterExpression("pat", SqlParameterType.String));
        var stmt = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null), like, null, null), null, null);

        var compiled = CompilerTestHelpers.GenerateSqlServer(stmt, new Dictionary<string, PolicyValue> { ["pat"] = new("50%_[x]\\", SqlParameterType.String) });

        Assert.Contains("LIKE @p0 ESCAPE '\\'", compiled.Sql);
        Assert.Equal("50\\%\\_\\[x]\\\\", compiled.Parameters[0].Value);
    }

    [Fact]
    public void PolicyLike_IntentionalPattern_IsNotEscaped_ButStillHasExplicitEscape()
    {
        var like = new LikeExpression(
            new ColumnReference(new SqlQualifiedName("n")),
            new PolicyParameterExpression("pat", SqlParameterType.String, ParameterOrigin.Policy, IsLikePattern: true));
        var stmt = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("t"), null), like, null, null), null, null);

        var compiled = CompilerTestHelpers.GenerateSqlServer(stmt, new Dictionary<string, PolicyValue> { ["pat"] = new("a%", SqlParameterType.String) });

        Assert.Contains("ESCAPE '\\'", compiled.Sql);
        Assert.Equal("a%", compiled.Parameters[0].Value);
    }

    // ---- verifier: no raw values in policy IR (INV-5) ----

    [Fact]
    public void Verifier_Rejects_LiteralValueInsideInjectedPredicate()
    {
        var id = new SecurityPredicateId("dbo.Orders", 0);
        SelectStatement Stmt(Expression inner) => new(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new SubqueryTableSource(new SelectStatement(null,
                    new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Id", true) })), new SqlIdentifier("Id", true)) },
                        new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Orders", true) }), null),
                        new SecurityPredicateExpression(inner, id, SecurityScope.Root), null, null), null, null),
                    new SqlIdentifier("o", true)), null, null, null), null, null);

        var verifier = new TrinoSqlEngine.Ast.Security.SecurityCoverageVerifier(_ => new TrinoSqlEngine.Ast.Security.TableCoverageRequirement(
            "dbo.Orders", "dbo", "Orders", ImmutableArray.Create(id), ImmutableArray.Create(id)));

        var bad = new BinaryExpression(new ColumnReference(new SqlQualifiedName("a")), BinaryOperator.Equal, new LiteralExpression("EU", LiteralType.String));
        Assert.Throws<TrinoSqlEngine.Ast.Security.SecurityCoverageException>(() => verifier.Verify(Stmt(bad), CancellationToken.None));

        var good = new BinaryExpression(new ColumnReference(new SqlQualifiedName("a")), BinaryOperator.Equal, new PolicyParameterExpression("p", SqlParameterType.String));
        verifier.Verify(Stmt(good), CancellationToken.None);

        var denyAll = new BinaryExpression(new LiteralExpression(1L, LiteralType.Integer), BinaryOperator.Equal, new LiteralExpression(0L, LiteralType.Integer));
        verifier.Verify(Stmt(denyAll), CancellationToken.None);
    }
}
