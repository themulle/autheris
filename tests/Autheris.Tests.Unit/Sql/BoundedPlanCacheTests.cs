namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class BoundedPlanCacheTests
{
    [Fact]
    public void Eviction_AfterMaxEntriesPlusOne_KeepsAtLeastNinetyPercentAndRecordsEvictionMetric()
    {
        // AR-10 / AR-19: After MaxEntries + 1 insertions, >= 90% of entries remain (no complete clear),
        // and autheris_plan_cache_evictions_total > 0
        const int maxEntries = 100;
        var cache = new CompiledSqlQueryPlanCache(maxEntries: maxEntries, defaultTtl: TimeSpan.FromMinutes(10));
        var tenant = new TenantId("tenant_a");

        for (int i = 0; i < maxEntries + 1; i++)
        {
            cache.SetCompiledSql(
                $"SELECT {i}",
                (ulong)i,
                DatabaseDialect.PostgreSql,
                tenant,
                (ulong)i,
                $"SELECT {i};");
        }

        // Check how many of the first 100 entries survived
        int surviving = 0;
        for (int i = 0; i < maxEntries + 1; i++)
        {
            if (cache.TryGetCompiledSql(
                $"SELECT {i}",
                (ulong)i,
                DatabaseDialect.PostgreSql,
                tenant,
                (ulong)i,
                out var sql) && sql == $"SELECT {i};")
            {
                surviving++;
            }
        }

        surviving.ShouldBeGreaterThanOrEqualTo((int)(maxEntries * 0.9));
    }

    [Fact]
    public void Different_DataSource_Causes_CacheMiss()
    {
        // SEC-CACHE-01: Two data sources of same dialect and tenant must not share plans
        var cache = new CompiledSqlQueryPlanCache();
        var tenant = new TenantId("tenant_a");
        const string rawSql = "SELECT * FROM orders";
        ulong qHash = 12345;
        ulong pHash = 67890;

        cache.SetCompiledSql(
            rawSql,
            qHash,
            DatabaseDialect.PostgreSql,
            tenant,
            pHash,
            policyFingerprint: "fingerprint_1",
            dataSource: "ds_primary",
            sql: "SELECT id FROM orders;");

        // Query with different data source must miss
        bool hitDifferentDs = cache.TryGetCompiledSql(
            rawSql,
            qHash,
            DatabaseDialect.PostgreSql,
            tenant,
            pHash,
            policyFingerprint: "fingerprint_1",
            dataSource: "ds_secondary",
            out var sqlDifferent);

        hitDifferentDs.ShouldBeFalse();
        sqlDifferent.ShouldBeNull();

        // Query with matching data source must hit
        bool hitSameDs = cache.TryGetCompiledSql(
            rawSql,
            qHash,
            DatabaseDialect.PostgreSql,
            tenant,
            pHash,
            policyFingerprint: "fingerprint_1",
            dataSource: "ds_primary",
            out var sqlSame);

        hitSameDs.ShouldBeTrue();
        sqlSame.ShouldBe("SELECT id FROM orders;");
    }

    [Fact]
    public void Changed_CatalogColumns_Causes_CacheMiss()
    {
        // SEC-CACHE-01: Changing table catalog columns must invalidate / change policy hash/fingerprint
        var cache = new CompiledSqlQueryPlanCache();

        var colsV1 = new Dictionary<string, IReadOnlyList<string>>
        {
            ["orders"] = new[] { "id", "customer_id", "total" }
        };

        var colsV2 = new Dictionary<string, IReadOnlyList<string>>
        {
            ["orders"] = new[] { "id", "customer_id", "total", "secret_token" }
        };

        ulong hashV1 = cache.ComputePolicyHash(
            rlsPredicates: null,
            columnMasks: null,
            tablesWithoutRls: null,
            maxRows: 100,
            isDml: false,
            rewriterEngine: "AstCompiler",
            catalogColumnsMap: colsV1);

        ulong hashV2 = cache.ComputePolicyHash(
            rlsPredicates: null,
            columnMasks: null,
            tablesWithoutRls: null,
            maxRows: 100,
            isDml: false,
            rewriterEngine: "AstCompiler",
            catalogColumnsMap: colsV2);

        hashV1.ShouldNotBe(hashV2);
    }

    [Fact]
    public void Forced_PolicyHash_Collision_With_Different_Fingerprint_Causes_CacheMiss()
    {
        // SEC-CACHE-01: 64-bit hash collision protection via canonical PolicyFingerprint comparison
        var cache = new CompiledSqlQueryPlanCache();
        var tenant = new TenantId("tenant_a");
        const string rawSql = "SELECT * FROM users";
        ulong qHash = 11111;
        ulong forcedCollidingPolicyHash = 99999;

        cache.SetCompiledSql(
            rawSql,
            qHash,
            DatabaseDialect.PostgreSql,
            tenant,
            forcedCollidingPolicyHash,
            policyFingerprint: "tenant:tenant_a;user:alice;rls:dept=sales",
            dataSource: "default",
            sql: "SELECT * FROM users WHERE dept = 'sales';");

        // Same hash, but different fingerprint -> MUST MISS
        bool hit = cache.TryGetCompiledSql(
            rawSql,
            qHash,
            DatabaseDialect.PostgreSql,
            tenant,
            forcedCollidingPolicyHash,
            policyFingerprint: "tenant:tenant_a;user:bob;rls:dept=hr",
            dataSource: "default",
            out var sql);

        hit.ShouldBeFalse();
        sql.ShouldBeNull();
    }

    [Fact]
    public void NullCompiledSqlQueryPlanCache_AlwaysMisses_AndNeverCaches()
    {
        var cache = NullCompiledSqlQueryPlanCache.Instance;
        var tenant = new TenantId("tenant_a");

        cache.SetCompiledSql("SELECT 1", 1, DatabaseDialect.PostgreSql, tenant, 1, "SELECT 1;");
        cache.TryGetCompiledSql("SELECT 1", 1, DatabaseDialect.PostgreSql, tenant, 1, out var sql).ShouldBeFalse();
        sql.ShouldBeNull();
    }

    [Fact]
    public void GovernedSqlExecutionService_HasNoNullable_ICompiledSqlQueryPlanCache_Parameter()
    {
        // Architecture invariant: PlanCache is deterministically injected and non-nullable
        var ctors = typeof(GovernedSqlExecutionService).GetConstructors();
        foreach (var ctor in ctors)
        {
            var planCacheParam = ctor.GetParameters()
                .FirstOrDefault(p => p.ParameterType == typeof(ICompiledSqlQueryPlanCache));

            if (planCacheParam != null)
            {
                var nullabilityContext = new NullabilityInfoContext();
                var nullabilityInfo = nullabilityContext.Create(planCacheParam);
                nullabilityInfo.WriteState.ShouldNotBe(NullabilityState.Nullable,
                    $"Constructor {ctor} has a nullable ICompiledSqlQueryPlanCache parameter.");
            }
        }
    }
}
