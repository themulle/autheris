using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>REQ-SQL-128: the list shape (never the values) is part of the template cache key (SEC-128-04, M-13).</summary>
public class ClientListCacheKeyTests
{
    private const string Sql = "SELECT id FROM orders WHERE id IN (__param_ids)";

    private readonly ClientListHarness _h = new();
    private readonly CompiledSqlTemplateCache _cache = new();

    private GovernedSqlCompiler Compiler() => new(_h.Engine, DialectCapabilityTableProvider(), _cache);

    private static TrinoSqlEngine.Ast.Capabilities.IDialectCapabilityProvider DialectCapabilityTableProvider() => new TableProvider();

    private sealed class TableProvider : TrinoSqlEngine.Ast.Capabilities.IDialectCapabilityProvider
    {
        public TrinoSqlEngine.Ast.Capabilities.DialectCapabilities Get(TargetSqlDialect dialect) =>
            TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(dialect);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CacheKey_ContainsListShape_AndDifferentLengthsAreDifferentTemplates()
    {
        var compiler = Compiler();
        var three = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 3))), CancellationToken.None);
        var five = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 5))), CancellationToken.None);

        Assert.Equal(3, ListSlots(three, "ids").Count);
        Assert.Equal(5, ListSlots(five, "ids").Count);
        Assert.NotEqual(three.Sql, five.Sql);
        Assert.Equal(0, _cache.Stats.Hits);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mutation_ShapeOmittedFromKey_ReusesTheTemplateOfAnotherLength()
    {
        // The control matters: without the shape in the key the second request would be served the first request's SQL text.
        var compiler = Compiler();
        using var seam = CompilerHooks.Use(new MutantHooks { OmittedCacheKeyFields = new HashSet<string> { "ClientLists" } });
        var three = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 3))), CancellationToken.None);
        var five = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 5))), CancellationToken.None);

        Assert.Equal(three.Sql, five.Sql);
        Assert.Equal(1, _cache.Stats.Hits);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Cache_UnreferencedListDoesNotChangeKey()
    {
        // The gateway drops unreferenced lists from the shape (SEC-128-04); equal shapes share one template.
        var compiler = Compiler();
        compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 3))), CancellationToken.None);
        compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("IDS", 3))), CancellationToken.None);
        Assert.Equal(1, _cache.Stats.Hits);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Cache_TwoRequests_SameLength_ShareTemplate_AndTemplateHoldsNoValues()
    {
        var compiler = Compiler();
        var first = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 2))), CancellationToken.None);
        var second = compiler.Compile(Sql.AsMemory(), _h.Request(TargetSqlDialect.PostgreSql, Shapes(("ids", 2))), CancellationToken.None);

        Assert.Equal(1, _cache.Stats.Hits);
        Assert.Equal(first.Sql, second.Sql);
        // List slots stay value-free in both the compiled output and the rebound template (values are bound per request).
        Assert.All(ListSlots(first, "ids").Concat(ListSlots(second, "ids")), s => Assert.Null(s.Value));
        Assert.All(ListSlots(second, "ids"), s => Assert.Equal(2, s.ListCount));
        Assert.DoesNotContain("ids:", first.ToString());
    }

    [Fact]
    public void Shapes_CanonicalIsLowerCasedSortedAndUnambiguous()
    {
        var shapes = Shapes(("B", 2), ("a", 10));
        Assert.Equal("a:10;b:2;", shapes.Canonical);
        Assert.Equal(string.Empty, ClientListShapes.Empty.Canonical);
        Assert.True(shapes.TryGetCount("A", out int a));
        Assert.Equal(10, a);
        Assert.False(shapes.TryGetCount("c", out _));
        Assert.Throws<ArgumentException>(() => Shapes(("a b", 1)));
        Assert.Throws<ArgumentException>(() => Shapes(("a", -1)));
        Assert.Throws<ArgumentException>(() => ClientListShapes.Create(new Dictionary<string, int> { ["a"] = 1, ["A"] = 2 }));
    }
}
