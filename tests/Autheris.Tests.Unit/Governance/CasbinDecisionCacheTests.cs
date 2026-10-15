namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Options;
using Microsoft.Extensions.Time.Testing;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class CasbinDecisionCacheTests
{
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");

    private static SecurityEvaluationContext Ctx(
        string[]? groups = null,
        string[]? columns = null,
        Dictionary<string, object?>? attributes = null,
        DatabaseDialect? dialect = null,
        string[]? tableColumns = null,
        string user = "S-1-5-21-u1")
        => new(
            new Sid(user),
            (groups ?? []).Select(g => new Sid(g)).ToArray(),
            new TenantId("t1"),
            Table,
            columns ?? [],
            IPAddress.Loopback,
            DateTimeOffset.UnixEpoch,
            "purpose",
            attributes,
            dialect)
        {
            TableColumns = tableColumns
        };

    [Fact]
    public void DecisionKey_DifferentTargetDialect_IsCacheMiss()
    {
        var pg = DecisionKey.Create(1, Ctx(dialect: DatabaseDialect.PostgreSql), "read");
        var sql = DecisionKey.Create(1, Ctx(dialect: DatabaseDialect.SqlServer), "read");
        var none = DecisionKey.Create(1, Ctx(dialect: null), "read");

        pg.Equals(sql).ShouldBeFalse();
        pg.Equals(none).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_DifferentTableColumns_IsCacheMiss()
    {
        var a = DecisionKey.Create(1, Ctx(tableColumns: ["id", "name"]), "read");
        var b = DecisionKey.Create(1, Ctx(tableColumns: ["id", "name", "ssn"]), "read");
        var c = DecisionKey.Create(1, Ctx(tableColumns: null), "read");

        a.Equals(b).ShouldBeFalse();
        a.Equals(c).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_GroupsWithSeparatorChars_DoNotCollide()
    {
        var joined = DecisionKey.Create(1, Ctx(groups: ["a,b"]), "read");
        var split = DecisionKey.Create(1, Ctx(groups: ["a", "b"]), "read");
        joined.Equals(split).ShouldBeFalse();

        var attrJoined = DecisionKey.Create(1, Ctx(attributes: new() { ["a"] = "1;b=2" }), "read");
        var attrSplit = DecisionKey.Create(1, Ctx(attributes: new() { ["a"] = "1", ["b"] = "2" }), "read");
        attrJoined.Equals(attrSplit).ShouldBeFalse();

        var colJoined = DecisionKey.Create(1, Ctx(columns: ["x,y"]), "read");
        var colSplit = DecisionKey.Create(1, Ctx(columns: ["x", "y"]), "read");
        colJoined.Equals(colSplit).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_DifferentEpochOrAction_IsCacheMiss()
    {
        DecisionKey.Create(1, Ctx(), "read").Equals(DecisionKey.Create(2, Ctx(), "read")).ShouldBeFalse();
        DecisionKey.Create(1, Ctx(), "read").Equals(DecisionKey.Create(1, Ctx(), "write")).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_EqualFields_EqualHash()
    {
        var a = DecisionKey.Create(7, Ctx(groups: ["g2", "g1"], columns: ["c2", "c1"], attributes: new() { ["b"] = 2, ["a"] = "x" }), "read");
        var b = DecisionKey.Create(7, Ctx(groups: ["g1", "g2"], columns: ["c1", "c2"], attributes: new() { ["a"] = "x", ["b"] = 2 }), "read");

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void DecisionKey_CollectionAttributeValues_DoNotCollide()
    {
        var a = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = new List<string> { "a", "b" } }), "read");
        var b = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = new List<string> { "a", "c" } }), "read");
        a.Equals(b).ShouldBeFalse();
    }

    private static DecisionKey Key(string user) => DecisionKey.Create(1, Ctx(user: user), "read");

    private static TableAccessDecision Decision(bool allowed) =>
        allowed
            ? TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(Table, "no");

    [Fact]
    public void Cache_AtCapacity_EvictsOneEntry_NotAll()
    {
        var cache = new CasbinDecisionCache(3, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        for (var i = 0; i < 3; i++)
        {
            cache.Set(Key($"u{i}"), Decision(true));
        }

        cache.Set(Key("u3"), Decision(true));

        cache.Count.ShouldBe(3);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed()
    {
        var cache = new CasbinDecisionCache(3, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        cache.Set(Key("u0"), Decision(true));
        cache.Set(Key("u1"), Decision(true));
        cache.Set(Key("u2"), Decision(true));

        cache.TryGet(Key("u0"), out _).ShouldBeTrue(); // u0 becomes most recent, u1 is the eviction candidate
        cache.Set(Key("u3"), Decision(true));

        cache.TryGet(Key("u1"), out _).ShouldBeFalse();
        cache.TryGet(Key("u0"), out _).ShouldBeTrue();
        cache.TryGet(Key("u2"), out _).ShouldBeTrue();
        cache.TryGet(Key("u3"), out _).ShouldBeTrue();
    }

    [Fact]
    public void CachedAllow_IsNotServed_BeyondTtl()
    {
        var time = new FakeTimeProvider();
        var cache = new CasbinDecisionCache(10, TimeSpan.FromSeconds(60), time);
        cache.Set(Key("u0"), Decision(true));

        time.Advance(TimeSpan.FromSeconds(60));
        cache.TryGet(Key("u0"), out var atTtl).ShouldBeTrue();
        atTtl.IsAllowed.ShouldBeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1));
        cache.TryGet(Key("u0"), out _).ShouldBeFalse();
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Service_CachedAllow_IsNotServed_BeyondTtl()
    {
        var time = new FakeTimeProvider();
        using var service = new CasbinEnforcementService(
            cacheOptions: new CasbinDecisionCacheOptions { TtlSeconds = 30 },
            timeProvider: time);
        var tenant = new TenantId("t1");
        service.AddPolicy(tenant, "S-1-5-21-u1", Table.ToString(), "read");
        var ctx = Ctx();

        (await service.EvaluatePolicyAsync(ctx)).IsAllowed.ShouldBeTrue();
        service.DiagnosticDecisionCacheCount.ShouldBe(1);

        (await service.EvaluatePolicyAsync(ctx)).IsAllowed.ShouldBeTrue();
        service.DiagnosticDecisionCacheHits.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(31));
        // Beyond the TTL the entry must not be served: the decision is recomputed (no additional hit) and cached again.
        (await service.EvaluatePolicyAsync(ctx)).IsAllowed.ShouldBeTrue();
        service.DiagnosticDecisionCacheHits.ShouldBe(1);
        service.DiagnosticDecisionCacheCount.ShouldBe(1);
    }

    [Fact]
    public async Task Service_SameContextDifferentDialect_IsNotServedFromCache()
    {
        using var service = new CasbinEnforcementService();
        service.AddPolicy(new TenantId("t1"), "S-1-5-21-u1", Table.ToString(), "read");

        await service.EvaluatePolicyAsync(Ctx(dialect: DatabaseDialect.PostgreSql));
        await service.EvaluatePolicyAsync(Ctx(dialect: DatabaseDialect.SqlServer));

        service.DiagnosticDecisionCacheCount.ShouldBe(2);
    }

    [Fact]
    public async Task Allocation_CacheHit_BelowBudget()
    {
        using var service = new CasbinEnforcementService();
        service.AddPolicy(new TenantId("t1"), "S-1-5-21-u1", Table.ToString(), "read");
        var ctx = Ctx(groups: ["S-1-5-21-g1", "S-1-5-21-g2"], columns: ["id", "name"], attributes: new() { ["department"] = "hr" });

        await service.EvaluatePolicyAsync(ctx); // populate
        for (var i = 0; i < 100; i++)
        {
            await service.EvaluatePolicyAsync(ctx); // warm up JIT
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            await service.EvaluatePolicyAsync(ctx);
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        perCall.ShouldBeLessThan(512, $"cache-hit path allocated {perCall} bytes per call");
    }

    [Fact]
    public void DecisionKey_NumericAttribute_DoesNotCollideWithStringOfSameText()
    {
        var number = DecisionKey.Create(1, Ctx(attributes: new() { ["level"] = 1.5 }), "read");
        var text = DecisionKey.Create(1, Ctx(attributes: new() { ["level"] = "1.5" }), "read");
        number.Equals(text).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_StringLookingLikeEncodedList_DoesNotCollideWithList()
    {
        var list = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = new List<string> { "a" } }), "read");
        var text = DecisionKey.Create(1, Ctx(attributes: new() { ["roles"] = "[1:a]" }), "read");
        list.Equals(text).ShouldBeFalse();
    }

    [Fact]
    public void DecisionKey_AttributeKeysDifferingByCaseOnly_AreNotCacheable()
    {
        var ambiguous = DecisionKey.Create(1, Ctx(attributes: new() { ["dept"] = "a", ["Dept"] = "b" }), "read");
        var plain = DecisionKey.Create(1, Ctx(attributes: new() { ["dept"] = "a", ["region"] = "b" }), "read");

        ambiguous.IsCacheable.ShouldBeFalse();
        plain.IsCacheable.ShouldBeTrue();
    }

    [Fact]
    public async Task Service_AmbiguousAttributeKeys_AreNeverCached()
    {
        using var service = new CasbinEnforcementService();
        // The rule reads the attributes, so they are part of the key (a rule that reads none shares one entry regardless of them).
        service.AddPolicy(new TenantId("t1"), "S-1-5-21-u1", Table.ToString(), "read", rlsFilter: "dept = ${department}");
        var ctx = Ctx(attributes: new() { ["dept"] = "a", ["Dept"] = "b" });

        await service.EvaluatePolicyAsync(ctx);
        await service.EvaluatePolicyAsync(ctx);

        service.DiagnosticDecisionCacheCount.ShouldBe(0);
        service.DiagnosticDecisionCacheHits.ShouldBe(0);
    }
}
