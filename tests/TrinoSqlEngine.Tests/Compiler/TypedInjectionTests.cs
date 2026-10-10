using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

public class TypedInjectionTests
{
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();
    private readonly InMemoryTableCatalog _catalog = Catalog();

    private TypedPolicyContext NewContext() => new(
        _catalog, _rowFilters, _masks,
        new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
        DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));

    private static PolicyPredicate RegionEu() => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });

    private SqlStatement Secure(string sql, TypedPolicyContext typed)
    {
        var ast = CompilerTestHelpers.Build(sql);
        var visitor = new AstSecurityVisitor(new RlsOptions { EnforceReadOnlyQueries = true }, new FastSqlEngine(), typed);
        return (SqlStatement)visitor.Visit(ast);
    }

    private static IEnumerable<SecurityPredicateExpression> Predicates(object? node)
    {
        if (node is null || node is string) yield break;
        if (node is SecurityPredicateExpression sp)
        {
            yield return sp;
            foreach (var inner in Predicates(sp.Predicate)) yield return inner;
            yield break;
        }

        if (node is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
                foreach (var m in Predicates(item)) yield return m;
            yield break;
        }

        if (node is SqlNode)
        {
            foreach (var p in node.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType.IsEnum) continue;
                foreach (var m in Predicates(p.GetValue(node))) yield return m;
            }
        }
    }

    [Fact]
    public void Table_IsWrapped_WithTenantAndPolicyPredicate_AndCanonicalName()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var typed = NewContext();

        var secured = (SelectStatement)Secure("SELECT id FROM orders WHERE status = 'x'", typed);

        var spec = (QuerySpecification)secured.Body;
        var sub = Assert.IsType<SubqueryTableSource>(spec.From);
        Assert.Equal("orders", sub.Alias.Value);   // user spelling stays the alias
        var inner = (QuerySpecification)sub.Subquery.Body;
        var named = Assert.IsType<NamedTableSource>(inner.From);
        Assert.Equal(new[] { "dbo", "Orders" }, named.Name.Parts.Select(p => p.Value));
        Assert.All(named.Name.Parts, p => Assert.True(p.IsQuoted));

        var ids = Predicates(inner.Where).Select(p => p.Id).ToList();
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), ids);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 1), ids);
        Assert.All(inner.Projections, p => Assert.IsType<ColumnSelectItem>(p));
        Assert.Equal(6, inner.Projections.Count);

        Assert.Contains(typed.AppliedPredicates, id => id == new SecurityPredicateId("dbo.Orders", 1));
        Assert.Equal("EU", typed.PolicyValues["__pol_region"].Value);
        Assert.Equal("acme", typed.PolicyValues["__autheris_tenant"].Value);
    }

    [Fact]
    public void TenantPredicate_IsBinaryExact_AndUsesBoundTenantParameter()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var secured = Secure("SELECT id FROM orders", NewContext());

        var tenant = Predicates(secured).First(p => p.Id.Ordinal == 0 && p.Scope != SecurityScope.PolicySubquery);
        var casts = new List<CastExpression>();
        Collect(tenant.Predicate, casts);
        Assert.NotEmpty(casts);                                            // CAST(... AS varbinary) conjunct
        Assert.Contains(casts, c => c.TargetType.StartsWith("varbinary", StringComparison.OrdinalIgnoreCase));

        var tenantParam = new List<PolicyParameterExpression>();
        Collect(tenant.Predicate, tenantParam);
        Assert.All(tenantParam, p =>
        {
            Assert.Equal("__autheris_tenant", p.Name);
            Assert.Equal(ParameterOrigin.Tenant, p.Origin);
        });
    }

    private static void Collect<T>(object? node, List<T> into) where T : class
    {
        if (node is null || node is string) return;
        if (node is T t) into.Add(t);
        if (node is System.Collections.IEnumerable list)
        {
            foreach (var item in list) Collect(item, into);
            return;
        }

        if (node is SqlNode)
        {
            foreach (var p in node.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType.IsEnum) continue;
                Collect(p.GetValue(node), into);
            }
        }
    }

    [Fact]
    public void TableWithoutTenantColumn_GetsOnlyThePolicyPredicate()
    {
        _rowFilters.Predicates[Lookup] = PolicyPredicate.DenyAll;
        var secured = Secure("SELECT code FROM lookup", NewContext());
        var ids = Predicates(secured).Select(p => p.Id).ToList();
        Assert.Equal(new[] { new SecurityPredicateId("dbo.Lookup", 1) }, ids);
    }

    [Fact]
    public void TableWithoutPolicy_StillGetsTenantPredicate_FailClosed()
    {
        _rowFilters.NoPolicy.Add(Orders);
        var secured = Secure("SELECT id FROM orders", NewContext());
        var ids = Predicates(secured).Select(p => p.Id).ToList();
        Assert.Equal(new[] { new SecurityPredicateId("dbo.Orders", 0) }, ids);
    }

    [Fact]
    public void UnknownTable_IsRejected()
    {
        Assert.Throws<SecurityException>(() => Secure("SELECT * FROM mystery", NewContext()));
    }

    [Fact]
    public void CteBody_IsSecured_CteReferenceIsNot()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var secured = (SelectStatement)Secure("WITH c AS (SELECT id FROM orders) SELECT id FROM c", NewContext());

        Assert.NotEmpty(Predicates(secured.With));
        var spec = (QuerySpecification)secured.Body;
        Assert.IsType<NamedTableSource>(spec.From);
    }

    [Fact]
    public void CteNamedLikeAPhysicalTable_BodyReference_IsStillSecuredAndQualified()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var typed = NewContext();
        var secured = Secure("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders", typed);

        var verifier = typed.CreateVerifier();
        verifier.Verify(secured, CancellationToken.None);   // does not throw
    }

    [Fact]
    public void PolicySubquery_TablesGetTheTenantPredicate_AndTargetAliasIsBound()
    {
        var entitlementRef = new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) });
        var correlated = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e", true), new SqlIdentifier("Id", true) })), null) },
                new NamedTableSource(entitlementRef, new SqlIdentifier("e", true)),
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e", true), new SqlIdentifier("OrderId", true) })),
                    BinaryOperator.Equal,
                    new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("autheris_target"), new SqlIdentifier("Id", true) }))),
                null, null), null, null));
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(correlated, new Dictionary<string, PolicyValue>());
        var typed = NewContext();

        var secured = (SelectStatement)Secure("SELECT id FROM orders", typed);

        var inner = (QuerySpecification)((SubqueryTableSource)((QuerySpecification)secured.Body).From!).Subquery.Body;
        Assert.Equal(RowFilterAliases.Target, ((NamedTableSource)inner.From!).Alias!.Value);

        var policySubqueryPredicates = Predicates(secured).Where(p => p.Scope == SecurityScope.PolicySubquery).ToList();
        Assert.Single(policySubqueryPredicates);
        Assert.Equal(new SecurityPredicateId("dbo.Entitlements", 0), policySubqueryPredicates[0].Id);

        typed.CreateVerifier().Verify(secured, CancellationToken.None);
    }

    [Fact]
    public void Verifier_FromContext_ProvesInjection_AndDetectsATamperedTree()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var typed = NewContext();
        var secured = Secure("SELECT o.id FROM orders o JOIN lookup l ON l.code = o.status", typed);
        var verifier = typed.CreateVerifier();

        var applied = verifier.Verify(secured, CancellationToken.None);
        Assert.Contains(new SecurityPredicateId("dbo.Orders", 0), applied);

        // Tamper: replace the injected WHERE of the Orders wrapper with the user's predicate.
        var stripped = (SelectStatement)new StripPredicates().Visit(secured);
        Assert.Throws<TrinoSqlEngine.Ast.Security.SecurityCoverageException>(() => verifier.Verify(stripped, CancellationToken.None));
    }

    private sealed class StripPredicates : SqlAstRewriter
    {
        public override SqlNode VisitQuerySpecification(QuerySpecification node)
        {
            var visited = (QuerySpecification)base.VisitQuerySpecification(node);
            return visited.From is NamedTableSource ? visited with { Where = null } : visited;
        }
    }

    [Fact]
    public void FullPipeline_ProducesBoundSql_WithNoPolicyValuesInText()
    {
        _rowFilters.Predicates[Orders] = RegionEu();
        var typed = NewContext();
        var secured = Secure("SELECT id FROM orders WHERE status = 'x'", typed);
        var generator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.SqlServer);

        var compiled = generator.Generate(secured,
            new ParameterSource(typed.PolicyValues.ToFrozenDictionaryForTest(), new Dictionary<string, object?>()), CancellationToken.None);

        Assert.DoesNotContain("acme", compiled.Sql);
        Assert.DoesNotContain("EU", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant && Equals(p.Value, "acme"));
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        Assert.Contains("[dbo].[Orders]", compiled.Sql);
    }
}

internal static class FrozenTestExtensions
{
    public static System.Collections.Frozen.FrozenDictionary<string, PolicyValue> ToFrozenDictionaryForTest(
        this IReadOnlyDictionary<string, PolicyValue> source) =>
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(source);
}
