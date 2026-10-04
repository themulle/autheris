namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class CompiledSqlQueryPlanCacheTests
{
    [Fact]
    public void ComputeHash_ConsistentAndDistinctAcrossOperations()
    {
        var cache = new CompiledSqlQueryPlanCache();
        var query = "query GetUsers { users { id name email } }";

        var hash1 = cache.ComputeHash(query, "GetUsers");
        var hash2 = cache.ComputeHash(query, "GetUsers");
        var hash3 = cache.ComputeHash(query, "DifferentOperation");
        var hash4 = cache.ComputeHash("query GetOther { other { id } }", "GetUsers");

        hash1.ShouldBe(hash2);
        hash1.ShouldNotBe(hash3);
        hash1.ShouldNotBe(hash4);
    }

    [Fact]
    public void ComputeRlsHash_OrderIndependentAndDistinguishesPredicates()
    {
        var cache = new CompiledSqlQueryPlanCache();
        var t1 = new TableIdentifier("default", "public", "orders");
        var t2 = new TableIdentifier("default", "public", "customers");

        var dictA = new Dictionary<TableIdentifier, string?>
        {
            [t1] = "orders.tenant_id = 't1'",
            [t2] = "customers.tenant_id = 't1'"
        };

        var dictB = new Dictionary<TableIdentifier, string?>
        {
            [t2] = "customers.tenant_id = 't1'",
            [t1] = "orders.tenant_id = 't1'"
        };

        var dictDifferent = new Dictionary<TableIdentifier, string?>
        {
            [t1] = "orders.tenant_id = 't2'",
            [t2] = "customers.tenant_id = 't2'"
        };

        var hashA = cache.ComputeRlsHash(dictA);
        var hashB = cache.ComputeRlsHash(dictB);
        var hashDiff = cache.ComputeRlsHash(dictDifferent);

        hashA.ShouldBe(hashB);
        hashA.ShouldNotBe(hashDiff);
        cache.ComputeRlsHash(null).ShouldBe(0UL);
    }

    [Fact]
    public void MultiTenantIsolation_EnforcesStrictTenantAndRlsSeparation_SecCache01()
    {
        var cache = new CompiledSqlQueryPlanCache();
        var query = "query GetOrders { orders { id total } }";
        var queryHash = cache.ComputeHash(query);

        var tenantA = new TenantId("tenant-alpha");
        var tenantB = new TenantId("tenant-beta");

        var rlsHashA = 100UL;
        var rlsHashB = 200UL;

        var sqlTenantA = "SELECT json_agg(...) FROM orders WHERE tenant_id = 'tenant-alpha'";
        var sqlTenantB = "SELECT json_agg(...) FROM orders WHERE tenant_id = 'tenant-beta'";

        cache.SetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantA, rlsHashA, sqlTenantA);
        cache.SetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantB, rlsHashB, sqlTenantB);

        // Verify Tenant A gets Tenant A's plan
        cache.TryGetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantA, rlsHashA, out var planA).ShouldBeTrue();
        planA.ShouldBe(sqlTenantA);

        // Verify Tenant B gets Tenant B's plan
        cache.TryGetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantB, rlsHashB, out var planB).ShouldBeTrue();
        planB.ShouldBe(sqlTenantB);

        // CRITICAL SEC-CACHE-01: Tenant B cannot access Tenant A's plan using Tenant A's rlsHash or vice versa
        cache.TryGetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantB, rlsHashA, out _).ShouldBeFalse();
        cache.TryGetCompiledSql(queryHash, DatabaseDialect.PostgreSql, tenantA, rlsHashB, out _).ShouldBeFalse();

        // Dialect isolation
        cache.TryGetCompiledSql(queryHash, DatabaseDialect.SqlServer, tenantA, rlsHashA, out _).ShouldBeFalse();
    }
}
