namespace TrinoSqlEngine.Tests;

using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class CompiledSqlTemplateCacheConcurrencyTests
{
    private static CompiledSqlTemplate CreateTemplate(string keyMaterial, string sql = "SELECT 1") =>
        new(
            keyMaterial,
            sql,
            ImmutableArray<SqlSlot>.Empty,
            TargetSqlDialect.PostgreSql,
            SqlStatementClass.Select,
            ImmutableArray<SecurityPredicateId>.Empty,
            "test-v1",
            ImmutableArray<TableDependency>.Empty);

    [Fact]
    public void LruEviction_HotEntrySurvivesColdTrafficBurst()
    {
        const int capacity = 16;
        var cache = new CompiledSqlTemplateCache(capacity);

        cache.Add(CreateTemplate("hot-key", "SELECT /* hot */ 1"));
        for (int i = 0; i < capacity - 1; i++)
        {
            cache.Add(CreateTemplate($"initial-cold-{i}", $"SELECT {i}"));
        }

        // Touch hot-key before cold burst so its access sequence is refreshed.
        Assert.NotNull(cache.Find("hot-key"));

        for (int i = 0; i < capacity * 4; i++)
        {
            Assert.True(cache.TryGet("hot-key", out var hit));
            Assert.NotNull(hit);
            cache.Add(CreateTemplate($"burst-cold-{i}", $"SELECT {i + 1000}"));
        }

        Assert.True(cache.Stats.Entries <= capacity);
        var survivingHot = cache.Find("hot-key");
        Assert.NotNull(survivingHot);
        Assert.Equal("SELECT /* hot */ 1", survivingHot!.Sql);
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_RemainBoundedAndThreadSafe()
    {
        const int capacity = 64;
        var cache = new CompiledSqlTemplateCache(capacity);

        for (int i = 0; i < 8; i++)
        {
            cache.Add(CreateTemplate($"pinned-{i}", $"SELECT {i}"));
        }

        var workers = Enumerable.Range(0, 8).Select(workerId => Task.Run(() =>
        {
            for (int iter = 0; iter < 250; iter++)
            {
                string pinnedKey = $"pinned-{iter % 8}";
                if (cache.TryGet(pinnedKey, out var template) && template is not null)
                {
                    cache.CountHit();
                    Assert.Equal(pinnedKey, template.KeyMaterial);
                }
                else
                {
                    cache.CountMiss();
                    cache.Add(CreateTemplate(pinnedKey, $"SELECT {iter % 8}"));
                }

                string coldKey = $"cold-{workerId}-{iter}";
                cache.Add(CreateTemplate(coldKey, $"SELECT {workerId}, {iter}"));
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.True(cache.Stats.Entries <= capacity);
        Assert.Equal(8 * 250, cache.Stats.Hits + cache.Stats.Misses);
    }

    [Fact]
    public void SqlNode_EnumerateChildren_ReturnsDirectAstChildrenWithoutPerNodeReflection()
    {
        var left = new ColumnReference(new SqlQualifiedName("t", "id"));
        var right = new LiteralExpression(42L, LiteralType.Integer);
        var binary = new BinaryExpression(left, BinaryOperator.Equal, right);

        var children = binary.EnumerateChildren().ToArray();

        Assert.Equal(2, children.Length);
        Assert.Same(left, children[0]);
        Assert.Same(right, children[1]);
        Assert.Empty(right.EnumerateChildren());
    }
}
