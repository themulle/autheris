using System.Collections.Immutable;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the value-free compile template and its cache: a template is rebound with the values of the current
/// request (never the values of the request that created it), page offsets are bounded and need their slot, the cache identity is the
/// full key material compared ordinally (a hash alone is never an identity), eviction drops the least recently used entry, and the
/// canonical key never contains the tenant value but does contain everything else that changes the output.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class CompiledSqlTemplateRuleTests
{
    private static readonly ImmutableArray<SecurityPredicateId> NoPredicates = ImmutableArray<SecurityPredicateId>.Empty;

    private static CompiledSqlTemplate Template(string key, string sql = "SELECT 1", params SqlSlot[] slots) =>
        new(key, sql, slots.ToImmutableArray(), TargetSqlDialect.SqlServer, SqlStatementClass.Select, NoPredicates, "test", ImmutableArray<TableDependency>.Empty);

    private static SqlSlot Slot(int ordinal, ParameterOrigin origin, string? source = null, object? literal = null, SqlParameterType type = SqlParameterType.String,
        bool escapeLike = false) => new($"@p{ordinal}", $"@p{ordinal}", ordinal, type, origin, source, literal, EscapeLikePattern: escapeLike);

    private static IReadOnlyDictionary<string, PolicyValue> Values(params (string Name, object? Value, SqlParameterType Type)[] items) =>
        items.ToDictionary(i => i.Name, i => new PolicyValue(i.Value, i.Type), StringComparer.Ordinal);

    [Fact]
    public void Rebind_UsesTheCurrentPolicyValues_NotTheOnesOfTheCreatingRequest()
    {
        var template = Template("k", "SELECT 1", Slot(0, ParameterOrigin.Tenant, "__t", "stale-tenant"), Slot(1, ParameterOrigin.Policy, "p", "stale"), Slot(2, ParameterOrigin.Mask, "m"));

        var compiled = template.Rebind(Values(("__t", "acme", SqlParameterType.String), ("p", "EU", SqlParameterType.String), ("m", "x", SqlParameterType.String)));

        Assert.Equal(new object?[] { "acme", "EU", "x" }, compiled.Parameters.Select(p => p.Value).ToArray());
        Assert.Equal(new[] { ParameterOrigin.Tenant, ParameterOrigin.Policy, ParameterOrigin.Mask }, compiled.Parameters.Select(p => p.Origin).ToArray());
        Assert.Equal(new[] { "__t", "p", "m" }, compiled.Parameters.Select(p => p.SourceName).ToArray());
        Assert.Equal(new[] { "@p0", "@p1", "@p2" }, compiled.Parameters.Select(p => p.Marker).ToArray());
        Assert.Equal("SELECT 1", compiled.Sql);
        Assert.Equal(TargetSqlDialect.SqlServer, compiled.Dialect);
        Assert.Equal("test", compiled.CompilerVersion);
    }

    [Fact]
    public void Rebind_KeepsQueryLiteralValues_AndAnExistingTypeForNonPolicySlots()
    {
        var template = Template("k", "SELECT 1", Slot(0, ParameterOrigin.QueryLiteral, literal: 42L, type: SqlParameterType.Int64),
            Slot(1, ParameterOrigin.ClientNamed, "foo", type: SqlParameterType.Int32));

        var compiled = template.Rebind(Values());

        Assert.Equal(42L, compiled.Parameters[0].Value);
        Assert.Equal(SqlParameterType.Int64, compiled.Parameters[0].Type);
        Assert.Null(compiled.Parameters[1].Value);
        Assert.Equal(SqlParameterType.Int32, compiled.Parameters[1].Type);
    }

    [Theory]
    [InlineData(ParameterOrigin.Tenant)]
    [InlineData(ParameterOrigin.Policy)]
    [InlineData(ParameterOrigin.Mask)]
    public void Rebind_FailsClosed_WhenAPolicyValueIsMissingOrOfAnotherType(ParameterOrigin origin)
    {
        var template = Template("k", "SELECT 1", Slot(0, origin, "v", type: SqlParameterType.String));

        const string Message = "A cached template could not be rebound with the current policy values.";
        Assert.Equal(Message, Assert.Throws<SecurityException>(() => template.Rebind(Values())).Message);
        Assert.Equal(Message, Assert.Throws<SecurityException>(() => template.Rebind(Values(("other", "x", SqlParameterType.String)))).Message);
        Assert.Equal(Message, Assert.Throws<SecurityException>(() => template.Rebind(Values(("v", 1, SqlParameterType.Int32)))).Message);

        var noSource = Template("k", "SELECT 1", new SqlSlot("@p0", "@p0", 0, SqlParameterType.String, origin, null, null));
        Assert.Equal(Message, Assert.Throws<SecurityException>(() => noSource.Rebind(Values(("v", "x", SqlParameterType.String)))).Message);
    }

    [Fact]
    public void Rebind_EscapesALikePatternAgainOnEveryHit_AndRequiresAString()
    {
        var template = Template("k", "SELECT 1", Slot(0, ParameterOrigin.Policy, "p", escapeLike: true));

        var compiled = template.Rebind(Values(("p", "50%_x", SqlParameterType.String)));
        Assert.Equal(LikePatternEscaper.Escape("50%_x"), compiled.Parameters[0].Value);
        Assert.NotEqual("50%_x", compiled.Parameters[0].Value);
        Assert.True(compiled.Parameters[0].EscapeLikePattern);

        var plain = Template("k", "SELECT 1", Slot(0, ParameterOrigin.Policy, "p"));
        Assert.Equal("50%_x", plain.Rebind(Values(("p", "50%_x", SqlParameterType.String))).Parameters[0].Value);

        var notString = Template("k", "SELECT 1", Slot(0, ParameterOrigin.Policy, "p", type: SqlParameterType.Int32, escapeLike: true));
        var ex = Assert.Throws<SecurityException>(() => notString.Rebind(Values(("p", 5, SqlParameterType.Int32))));
        Assert.Equal("A LIKE pattern parameter must be a string.", ex.Message);
    }

    [Fact]
    public void Rebind_PageOffset_IsBoundedAndNeedsItsSlot()
    {
        var withSlot = Template("k", "SELECT 1", Slot(0, ParameterOrigin.PageOffset, type: SqlParameterType.Int64));

        Assert.Equal(7L, withSlot.Rebind(Values(), 7).Parameters[0].Value);
        Assert.Equal(0L, withSlot.Rebind(Values(), 0).Parameters[0].Value);
        Assert.Equal(1_000_000L, withSlot.Rebind(Values(), 1_000_000).Parameters[0].Value);
        Assert.Equal(5L, withSlot.Rebind(Values(), 5, maxOffset: 5).Parameters[0].Value);

        Assert.Equal("Page offset value is outside permitted bounds.", Assert.Throws<SecurityException>(() => withSlot.Rebind(Values(), -1)).Message);
        Assert.Equal("Page offset value is outside permitted bounds.", Assert.Throws<SecurityException>(() => withSlot.Rebind(Values(), 1_000_001)).Message);
        Assert.Equal("Page offset value is outside permitted bounds.", Assert.Throws<SecurityException>(() => withSlot.Rebind(Values(), 6, maxOffset: 5)).Message);
        Assert.Equal("A PageOffset slot requires a server-issued offset value.", Assert.Throws<SecurityException>(() => withSlot.Rebind(Values())).Message);

        var withoutSlot = Template("k", "SELECT 1");
        Assert.Equal("A page offset value was supplied for a template without a PageOffset slot.",
            Assert.Throws<SecurityException>(() => withoutSlot.Rebind(Values(), 1)).Message);
        Assert.Empty(withoutSlot.Rebind(Values()).Parameters);
    }

    [Fact]
    public void Rebind_CarriesTheStatementFacts()
    {
        var template = Template("k") with
        {
            ExpectedAffectedRows = 3,
            HasRecursiveCte = true,
            OutputColumnMasks = ImmutableArray.Create(true, false),
            StatementClass = SqlStatementClass.Insert,
            AppliedPredicates = ImmutableArray.Create(new SecurityPredicateId("dbo.t", 1)),
            Dialect = TargetSqlDialect.PostgreSql
        };

        var compiled = template.Rebind(Values());

        Assert.Equal(3, compiled.ExpectedAffectedRows);
        Assert.True(compiled.HasRecursiveCte);
        Assert.Equal(new[] { true, false }, compiled.OutputColumnMasks.ToArray());
        Assert.Equal(SqlStatementClass.Insert, compiled.StatementClass);
        Assert.Equal(TargetSqlDialect.PostgreSql, compiled.Dialect);
        Assert.Equal(new SecurityPredicateId("dbo.t", 1), Assert.Single(compiled.AppliedPredicates));

        var defaults = Template("k").Rebind(Values());
        Assert.Null(defaults.ExpectedAffectedRows);
        Assert.False(defaults.HasRecursiveCte);
        Assert.Empty(defaults.OutputColumnMasks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("k")]
    [InlineData("key with äöü and € and \U0001F600")]
    public void HashOf_IsTheUppercaseSha256OfTheUtf8Key(string key)
    {
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), CompiledSqlTemplateCache.HashOf(key));
    }

    [Theory]
    [InlineData(340)]
    [InlineData(341)]
    [InlineData(342)]
    [InlineData(5000)]
    public void HashOf_IsCorrectOnBothSidesOfTheStackBufferLimit(int length)
    {
        string key = new string('a', length - 1) + "€";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), CompiledSqlTemplateCache.HashOf(key));
        string ascii = new string('b', length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ascii))), CompiledSqlTemplateCache.HashOf(ascii));
    }

    [Fact]
    public void Find_ComparesTheFullKeyMaterial_NotJustTheHash()
    {
        var cache = new CompiledSqlTemplateCache(8);
        var template = Template("material-a");
        cache.Add(template);

        Assert.Same(template, cache.Find("material-a"));
        Assert.True(cache.TryGet("material-a", out var found));
        Assert.Same(template, found);
        Assert.Null(cache.Find("material-b"));
        Assert.False(cache.TryGet("material-b", out var missing));
        Assert.Null(missing);

        // a template stored under the hash of another key never answers for that key (hash collision defence)
        var other = Template("material-c");
        cache.Add(other, CompiledSqlTemplateCache.HashOf("material-a-collides"));
        Assert.Null(cache.Find("material-a-collides"));
        Assert.Same(other, cache.Find("material-c", CompiledSqlTemplateCache.HashOf("material-a-collides")));
    }

    [Fact]
    public void Add_WithAZeroOrNegativeBound_StoresNothing()
    {
        foreach (int bound in new[] { 0, -1 })
        {
            var cache = new CompiledSqlTemplateCache(bound);
            cache.Add(Template("k"));
            Assert.Null(cache.Find("k"));
            Assert.Equal(0, cache.Stats.Entries);
            Assert.Empty(cache.Snapshot());
        }
    }

    [Fact]
    public void Add_EvictsTheLeastRecentlyUsedEntry_AndKeepsTheBound()
    {
        var cache = new CompiledSqlTemplateCache(3);
        cache.Add(Template("a"));
        cache.Add(Template("b"));
        cache.Add(Template("c"));
        Assert.Equal(3, cache.Stats.Entries);

        // touching a makes b the least recently used
        Assert.NotNull(cache.Find("a"));
        cache.Add(Template("d"));

        Assert.Equal(3, cache.Stats.Entries);
        Assert.Null(cache.Find("b"));
        Assert.NotNull(cache.Find("a"));
        Assert.NotNull(cache.Find("c"));
        Assert.NotNull(cache.Find("d"));

        // c, a, d were touched in that order: c is now the oldest
        Assert.NotNull(cache.Find("c"));
        Assert.NotNull(cache.Find("a"));
        Assert.NotNull(cache.Find("d"));
        cache.Add(Template("e"));
        Assert.Null(cache.Find("c"));
        Assert.Equal(new[] { "a", "d", "e" }, cache.Snapshot().Select(t => t.KeyMaterial).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Add_AtTheBound_DoesNotEvict_AndReplacingAKeyDoesNotGrowTheCache()
    {
        var cache = new CompiledSqlTemplateCache(2);
        cache.Add(Template("a"));
        cache.Add(Template("b"));
        Assert.Equal(2, cache.Stats.Entries);
        Assert.NotNull(cache.Find("a"));
        Assert.NotNull(cache.Find("b"));

        var replacement = Template("a", "SELECT 2");
        cache.Add(replacement);
        Assert.Equal(2, cache.Stats.Entries);
        Assert.Same(replacement, cache.Find("a"));
        Assert.NotNull(cache.Find("b"));
    }

    [Fact]
    public void Stats_CountHitsMissesAndEntries()
    {
        var cache = new CompiledSqlTemplateCache(4);
        Assert.Equal(new CompileCacheStats(0, 0, 0), cache.Stats);

        cache.CountMiss();
        cache.CountMiss();
        cache.CountHit();
        cache.Add(Template("a"));

        Assert.Equal(new CompileCacheStats(1, 2, 1), cache.Stats);
    }

    private static CompileRequest Request(string tenantValue = "acme", string tenantParameter = "__t", SqlParameterType tenantType = SqlParameterType.String) => new()
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = new DictPolicyProvider(),
            Masks = new DictMaskProvider(),
            Catalog = new InMemoryTableCatalog(Array.Empty<TableCatalogEntry>(), "dbo"),
            Tenant = new TenantBinding(tenantParameter, tenantValue, tenantType)
        }
    };

    private static string Material(CompileRequest request, string sql = "SELECT 1", int maxQuery = 0) =>
        CompileCacheKey.Material(request, sql, DialectCapabilityTable.Default.Get(request.TargetDialect), maxQuery);

    [Fact]
    public void KeyMaterial_NeverContainsTheTenantValue_ButEverythingElseThatChangesTheOutput()
    {
        string baseline = Material(Request());

        Assert.Equal(baseline, Material(Request(tenantValue: "other-tenant")));
        Assert.DoesNotContain("acme", baseline, StringComparison.Ordinal);
        Assert.Contains("tenant(__t,String)", baseline, StringComparison.Ordinal);
        Assert.NotEqual(baseline, Material(Request(tenantParameter: "__u")));
        Assert.NotEqual(baseline, Material(Request(tenantType: SqlParameterType.Int32)));
        Assert.NotEqual(baseline, Material(Request() with { TargetDialect = TargetSqlDialect.PostgreSql }));
        Assert.NotEqual(baseline, Material(Request(), "SELECT 2"));
        Assert.NotEqual(baseline, Material(Request(), maxQuery: 100));
    }

    [Fact]
    public void KeyMaterial_EndsWithTheLimitAndTheLengthPrefixedSql()
    {
        string material = Material(Request(), "SELECT 'x'", 123);

        Assert.EndsWith(";maxq=123;sql=10:SELECT 'x'", material, StringComparison.Ordinal);
        Assert.StartsWith("v=" + CompilerInfo.Version + ";cap=" + DialectCapabilities.TableVersion + ";", material, StringComparison.Ordinal);
        Assert.StartsWith("v=", Material(Request()), StringComparison.Ordinal);
        Assert.NotEqual(Material(Request(), "ab"), Material(Request(), "a"));
    }

    [Fact]
    public void KeyMaterial_RefusesNullArguments()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer);
        Assert.Throws<ArgumentNullException>(() => CompileCacheKey.Material(null!, "SELECT 1", caps));
        Assert.Throws<ArgumentNullException>(() => CompileCacheKey.Material(Request(), null!, caps));
        Assert.Throws<ArgumentNullException>(() => CompileCacheKey.Material(Request(), "SELECT 1", null!));
    }

    [Fact]
    public void KeyMaterial_SerializesTheStatementPermissionsAndLimits()
    {
        string baseline = Material(Request());

        Assert.NotEqual(baseline, Material(Request() with { Statements = StatementPermissions.Insert }));
        Assert.NotEqual(baseline, Material(Request() with { AllowRecursiveCte = true }));
        Assert.NotEqual(baseline, Material(Request() with { MaxRecursionDepth = 7 }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rebind_KeysetValues_MissingOrMistyped_FailsClosed()
    {
        var template = Template("k", "SELECT 1",
            Slot(0, ParameterOrigin.KeysetSeek, "keyset:0", type: SqlParameterType.Int32),
            Slot(1, ParameterOrigin.KeysetSeek, "keyset:1", type: SqlParameterType.String));

        var ok = template.Rebind(Values(), keysetValues: ImmutableArray.Create<object>(5, "x"));
        Assert.Equal(5, ok.Parameters[0].Value);
        Assert.Equal("x", ok.Parameters[1].Value);

        Assert.Throws<SecurityException>(() => template.Rebind(Values()));
        Assert.Throws<SecurityException>(() => template.Rebind(Values(), keysetValues: ImmutableArray.Create<object>(5)));
        Assert.Throws<SecurityException>(() => template.Rebind(Values(), keysetValues: ImmutableArray.Create<object>(5L, "x")));
        Assert.Throws<SecurityException>(() => template.Rebind(Values(), keysetValues: ImmutableArray.Create<object>(5, 6)));
        Assert.Throws<SecurityException>(() => template.Rebind(Values(), keysetValues: default(ImmutableArray<object>)));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rebind_KeysetValuesWithoutSlots_FailsClosed()
    {
        var withoutSlot = Template("k", "SELECT 1");

        Assert.Throws<SecurityException>(() => withoutSlot.Rebind(Values(), keysetValues: ImmutableArray.Create<object>(1)));
        Assert.Empty(withoutSlot.Rebind(Values()).Parameters);
    }
}
