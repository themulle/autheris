namespace Autheris.Tests.Unit.Sql;

using System.Collections.Immutable;
using System.Threading;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// L-16: the bounded-cache properties of the former <c>CompiledSqlQueryPlanCache</c> (AR-10 / AR-19 bound, SEC-CACHE-01 collision
/// protection), asserted on the engine's value-free template cache that replaced it.
/// </summary>
public sealed class CompiledSqlTemplateCacheTests
{
    private static CompiledSqlTemplate Template(string keyMaterial, string sql = "SELECT 1") =>
        new(keyMaterial, sql, ImmutableArray<SqlSlot>.Empty, TargetSqlDialect.PostgreSql, SqlStatementClass.Select,
            ImmutableArray<SecurityPredicateId>.Empty, "test", ImmutableArray<TableDependency>.Empty);

    [Fact]
    public void Eviction_AfterMaxEntriesPlusOne_StaysBounded_AndKeepsTheNewestEntries()
    {
        const int maxEntries = 100;
        var cache = new CompiledSqlTemplateCache(maxEntries);

        for (int i = 0; i < maxEntries + 1; i++)
        {
            cache.Add(Template($"key-{i}", $"SELECT {i}"));
        }

        cache.Stats.Entries.ShouldBe(maxEntries);
        int surviving = 0;
        for (int i = 0; i < maxEntries + 1; i++)
        {
            if (cache.Find($"key-{i}")?.Sql == $"SELECT {i}") surviving++;
        }

        surviving.ShouldBeGreaterThanOrEqualTo((int)(maxEntries * 0.9));
        cache.Find($"key-{maxEntries}").ShouldNotBeNull();
    }

    [Fact]
    public void ForcedHashCollision_WithDifferentKeyMaterial_IsAMiss()
    {
        var cache = new CompiledSqlTemplateCache();
        cache.Add(Template("tenant:a;user:alice;rls:dept=sales", "SELECT a"), "forced-hash");

        cache.Find("tenant:a;user:alice;rls:dept=sales", "forced-hash").ShouldNotBeNull();
        cache.Find("tenant:a;user:bob;rls:dept=hr", "forced-hash").ShouldBeNull();
    }

    [Fact]
    public void ADifferentRequestKey_NeverHits()
    {
        var cache = new CompiledSqlTemplateCache();
        cache.Add(Template("dialect=PostgreSql;sql=SELECT 1"));

        cache.Find("dialect=SqlServer;sql=SELECT 1").ShouldBeNull();
    }

    private static CompiledSql CompileOn(FastSqlEngine engine, TestCompileEnv catalogOwner, string sql = "SELECT id FROM main.orders") =>
        engine.Compile(sql.AsMemory(), catalogOwner.Request(), CancellationToken.None);

    /// <summary>
    /// L-16 (replaces the former "different data source" case): two sources expose the same table name with a different shape. One engine
    /// (one template cache) serves both; the second catalog must miss, never be answered from the template of the first.
    /// </summary>
    [Fact]
    public void ADifferentDataSource_WithTheSameTableName_NeverServesTheOtherSourcesTemplate()
    {
        var engine = new FastSqlEngine();
        var sourceA = new TestCompileEnv(TargetSqlDialect.PostgreSql, "main").Table("orders", "tenant_id", "id", "tenant_id", "name");
        var sourceB = new TestCompileEnv(TargetSqlDialect.PostgreSql, "main").Table("orders", "tenant_id", "id", "tenant_id", "email");

        var first = CompileOn(engine, sourceA);
        CompileOn(engine, sourceA).Sql.ShouldBe(first.Sql);
        engine.CompileCache.Stats.Hits.ShouldBe(1);   // control: the same catalog is a hit
        long missesBefore = engine.CompileCache.Stats.Misses;

        var other = CompileOn(engine, sourceB);

        engine.CompileCache.Stats.Misses.ShouldBe(missesBefore + 1);
        engine.CompileCache.Stats.Hits.ShouldBe(1);
        first.Sql.ShouldContain("\"name\"");
        other.Sql.ShouldContain("\"email\"");
        other.Sql.ShouldNotContain("\"name\"");
    }

    /// <summary>L-16 (replaces the former "changed catalog columns" case): a catalog change must not be served from a stale template.</summary>
    [Fact]
    public void ChangedCatalogColumns_MissTheCache_AndAreNotServedStale()
    {
        var engine = new FastSqlEngine();
        var before = new TestCompileEnv(TargetSqlDialect.PostgreSql, "main").Table("orders", "tenant_id", "id", "tenant_id", "name");
        var after = new TestCompileEnv(TargetSqlDialect.PostgreSql, "main").Table("orders", "tenant_id", "id", "tenant_id", "name", "extra");

        CompileOn(engine, before, "SELECT * FROM main.orders");
        long misses = engine.CompileCache.Stats.Misses;

        var changed = CompileOn(engine, after, "SELECT * FROM main.orders");

        engine.CompileCache.Stats.Misses.ShouldBe(misses + 1);
        engine.CompileCache.Stats.Hits.ShouldBe(0);
        changed.Sql.ShouldContain("\"extra\"");
    }
}
