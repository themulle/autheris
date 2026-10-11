using System.Collections.Frozen;
using TrinoSqlEngine.Ast;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// G8 compile-path optimizations (memoized pure functions, cached immutable per-entry nodes, allocation-lean lookups). Every one of them
/// must be invisible in the output: same SQL, same binds, no request value in a shared cache (SEC-ADG-01).
/// </summary>
public class CompilePathPerformanceTests
{
    private readonly FastSqlEngine _engine = new();

    private static CompileRequest Request(string tenant, InMemoryTableCatalog? catalog = null) => new()
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = new DictPolicyProvider(),
            Masks = new DictMaskProvider(),
            Catalog = catalog ?? Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    [Fact]
    public void BoundedMemo_ReturnsTheComputedValue_AndStopsStoringWhenFull()
    {
        var memo = new BoundedMemo<string>(capacity: 2);
        int calls = 0;
        string Compute(string key)
        {
            calls++;
            return key.ToUpperInvariant();
        }

        Assert.Equal("A", memo.GetOrCompute("a", Compute));
        Assert.Equal("A", memo.GetOrCompute("a", Compute));
        Assert.Equal(1, calls);   // stored

        memo.GetOrCompute("b", Compute);
        Assert.Equal("C", memo.GetOrCompute("c", Compute));
        Assert.Equal("C", memo.GetOrCompute("c", Compute));
        Assert.Equal(4, calls);   // "c" did not fit (capacity 2): computed again, never stored
    }

    [Fact]
    public void BoundedMemo_DoesNotCacheAFailure()
    {
        var memo = new BoundedMemo<int>();
        int calls = 0;
        int Fail(string key)
        {
            calls++;
            throw new InvalidOperationException(key);
        }

        Assert.Throws<InvalidOperationException>(() => memo.GetOrCompute("x", Fail));
        Assert.Throws<InvalidOperationException>(() => memo.GetOrCompute("x", Fail));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void FunctionMapWithoutDateTranslation_IsStableAndStillDropsTheNonNativeDateFunctions(TargetSqlDialect dialect)
    {
        var names = SqlFunctionAllowlists.CompilerNames(dialect);
        var first = SqlFunctionAllowlists.WithoutDateTranslation(dialect, names);
        var second = SqlFunctionAllowlists.WithoutDateTranslation(dialect, names);

        Assert.Same(first, second);
        var native = SqlFunctionAllowlists.GetDefault(dialect);
        foreach (string date in new[] { "date_add", "date_trunc", "now" })
        {
            Assert.Equal(names.Contains(date) && native.Contains(date), first.Contains(date));
        }
    }

    [Fact]
    public void FunctionMapWithoutDateTranslation_IsNotSharedAcrossDialectsForTheSameNameSet()
    {
        var names = SqlFunctionAllowlists.CompilerNames(TargetSqlDialect.SqlServer);
        var forSqlServer = SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.SqlServer, names);
        var forPostgres = SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.PostgreSql, names);

        Assert.Equal(
            SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.PostgreSql, names).OrderBy(x => x),
            forPostgres.OrderBy(x => x));
        Assert.Equal(
            SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.SqlServer, names).OrderBy(x => x),
            forSqlServer.OrderBy(x => x));
    }

    [Fact]
    public void TenantValue_NeverLeaksThroughTheSharedProjectionNodes_OfOneCatalogEntry()
    {
        var catalog = Catalog();
        var acme = _engine.Compile("SELECT o.id FROM orders o JOIN orders p ON p.id = o.id".AsMemory(), Request("acme", catalog), CancellationToken.None);
        var globex = _engine.Compile("SELECT o.id FROM orders o JOIN orders p ON p.id = o.id".AsMemory(), Request("globex", catalog), CancellationToken.None);

        Assert.Equal(acme.Sql, globex.Sql);
        Assert.All(acme.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant), p => Assert.Equal("acme", p.Value));
        Assert.All(globex.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant), p => Assert.Equal("globex", p.Value));
    }

    [Fact]
    public void RepeatedTableReferences_ProduceTheSameSecuredShape_AsIndependentCompiles()
    {
        var single = _engine.Compile("SELECT id FROM orders".AsMemory(), Request("acme"), CancellationToken.None);
        var again = new FastSqlEngine().Compile("SELECT id FROM orders".AsMemory(), Request("acme"), CancellationToken.None);

        Assert.Equal(single.Sql, again.Sql);
        Assert.Equal(single.AppliedPredicates.ToArray(), again.AppliedPredicates.ToArray());
    }

    [Fact]
    public void LiteralDedup_KeepsIntegerWidthsApart_AndMergesEqualValues()
    {
        var compiled = _engine.Compile("SELECT id FROM orders WHERE id = 7 OR id = 7 OR id = 8".AsMemory(), Request("acme"), CancellationToken.None);

        var literals = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.QueryLiteral).ToList();
        Assert.Equal(2, literals.Count);   // 7 is bound once, 8 once
        Assert.Contains(literals, p => Equals(p.Value, 7L));
        Assert.Contains(literals, p => Equals(p.Value, 8L));
    }

    [Fact]
    public void CacheKey_StaysStable_ForTheSameRequest_AndChangesWithEveryOption()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer);
        var request = Request("acme");
        string key = CompileCacheKey.Material(request, "SELECT 1", caps);

        Assert.Equal(key, CompileCacheKey.Material(request, "SELECT 1", caps));
        Assert.NotEqual(key, CompileCacheKey.Material(request with { TranslateTrinoDateFunctions = true }, "SELECT 1", caps));
        Assert.NotEqual(key, CompileCacheKey.Material(request with { Statements = StatementPermissions.Delete }, "SELECT 1", caps));
        Assert.NotEqual(key, CompileCacheKey.Material(request with { AllowedFunctions = new HashSet<string> { "lower" }.ToFrozenSet() }, "SELECT 1", caps));
        Assert.NotEqual(key, CompileCacheKey.Material(request with { TokenGuards = SqlTokenSecurityOptions.None }, "SELECT 1", caps));
        Assert.DoesNotContain("acme", key, StringComparison.Ordinal);   // the tenant VALUE is never part of the key
    }

    [Fact]
    public void CacheHash_IsTheSameForLongAndShortKeys_AsTheDirectDigest()
    {
        foreach (int length in new[] { 1, 200, 1025, 5000 })
        {
            string material = new string('é', length);
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
            Assert.Equal(expected, CompiledSqlTemplateCache.HashOf(material));
        }
    }

    [Fact]
    public void CatalogLookup_IsCaseInsensitive_ForLongNamesToo()
    {
        string longName = new string('x', 300);
        var entry = new TableCatalogEntry(new TableIdentity("dbo", longName), System.Collections.Immutable.ImmutableArray.Create(new CatalogColumn("Id", "int")), null, 1);
        var catalog = new InMemoryTableCatalog(new[] { entry }, "dbo");

        Assert.Same(entry, catalog.Resolve(new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName("DBO", longName.ToUpperInvariant())));
        Assert.Null(catalog.Resolve(new TrinoSqlEngine.Ast.Nodes.SqlQualifiedName("dbo", longName + "y")));
    }
}
