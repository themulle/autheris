namespace Autheris.Tests.Unit.Sql;

using System.Collections.Immutable;
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
}
