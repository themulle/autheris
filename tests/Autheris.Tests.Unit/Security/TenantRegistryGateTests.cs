namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Unit.TestDoubles;
using Shouldly;
using Xunit;

/// <summary>SEC-X1-13: positive-only cache, no cached allow for collisions or outages, per-issuer budget for new registrations.</summary>
[Trait("Category", "Security")]
public sealed class TenantRegistryGateTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly TenantId Acme = new("acme");

    private static TenantRegistryGate Gate(ITenantRegistry inner, ManualClock? clock = null, int perIssuer = 2, int maxCache = 100, int maxIssuers = 1024) =>
        new(inner, new TenantRegistryGateOptions(perIssuer, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), maxCache, maxIssuers), clock ?? new ManualClock());

    [Fact]
    public async Task PositiveResult_IsCached_UntilTheTtlExpires()
    {
        var clock = new ManualClock();
        var inner = new StubTenantRegistry();
        var gate = Gate(inner, clock);

        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Existing);
        inner.RegisterCalls.Count(c => c.Tenant == Acme && c.Issuer == "iss").ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(31));
        await gate.RegisterOrGetAsync(Acme, "iss", default);
        inner.RegisterCalls.Count(c => c.Tenant == Acme && c.Issuer == "iss").ShouldBe(2);
    }

    [Fact]
    public async Task Collision_IsNeverCached_AndEvictsTheCachedSiblingSpelling()
    {
        var lower = new TenantId("acme");
        var upper = new TenantId("ACME");
        var lowerCalls = 0;
        var inner = new StubTenantRegistry
        {
            OnRegisterOrGet = (t, _, _) =>
            {
                if (t == lower) return ValueTask.FromResult(lowerCalls++ == 0 ? TenantRegistration.Registered : TenantRegistration.Collision);
                if (t == upper) return ValueTask.FromResult(TenantRegistration.Collision);
                return ValueTask.FromResult(TenantRegistration.Registered);
            }
        };
        var gate = Gate(inner, perIssuer: 10);

        (await gate.RegisterOrGetAsync(lower, "iss", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(upper, "iss", default)).ShouldBe(TenantRegistration.Collision);
        // the cached allow for the first spelling is gone: the store is asked again and denies
        (await gate.RegisterOrGetAsync(lower, "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await gate.RegisterOrGetAsync(upper, "iss", default)).ShouldBe(TenantRegistration.Collision);
        inner.RegisterCalls.Count(c => c.Tenant == upper).ShouldBe(2);
    }

    [Fact]
    public async Task RegistryUnavailable_NotCachedAsAllow()
    {
        var calls = 0;
        var inner = new StubTenantRegistry
        {
            OnRegisterOrGet = (_, _, _) => calls++ == 0
                ? throw new InvalidOperationException("down")
                : ValueTask.FromResult(TenantRegistration.Collision)
        };
        var gate = Gate(inner);

        await Should.ThrowAsync<InvalidOperationException>(async () => await gate.RegisterOrGetAsync(Acme, "iss", default));
        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Collision);
        inner.RegisterCalls.Count(c => c.Tenant == Acme).ShouldBe(2);
    }

    [Fact]
    public async Task RegistrationRateLimit_PerIssuer()
    {
        var clock = new ManualClock();
        var inner = new StubTenantRegistry();
        var gate = Gate(inner, clock, perIssuer: 2);

        (await gate.RegisterOrGetAsync(new TenantId("t1"), "evil", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("t2"), "evil", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("t3"), "evil", default)).ShouldBe(TenantRegistration.RateLimited);
        inner.RegisterCalls.ShouldNotContain(c => c.Tenant == new TenantId("t3"));

        // another issuer is unaffected, an already cached tenant of the limited issuer still passes
        (await gate.RegisterOrGetAsync(new TenantId("t4"), "good", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("t1"), "evil", default)).ShouldBe(TenantRegistration.Existing);

        clock.Advance(TimeSpan.FromMinutes(1.1));
        (await gate.RegisterOrGetAsync(new TenantId("t3"), "evil", default)).ShouldBe(TenantRegistration.Registered);
    }

    // ---- Phase 5 L6 (X1): the existence check comes before the budget ---------------------------------------------------------

    [Fact]
    public async Task ExistingTenant_IsNotRateLimited_WhenTheIssuerBudgetIsExhausted()
    {
        var inner = new StubTenantRegistry
        {
            OnTryGet = (t, _) => ValueTask.FromResult<TenantRegistration?>(t == new TenantId("known") ? TenantRegistration.Existing : null)
        };
        var gate = Gate(inner, perIssuer: 1);

        (await gate.RegisterOrGetAsync(new TenantId("new1"), "busy", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("new2"), "busy", default)).ShouldBe(TenantRegistration.RateLimited);   // the budget is gone

        // a tenant that merely exists (another node registered it, or the cache expired) is a lookup, not a registration
        (await gate.RegisterOrGetAsync(new TenantId("known"), "busy", default)).ShouldBe(TenantRegistration.Existing);
        inner.RegisterCalls.ShouldNotContain(c => c.Tenant == new TenantId("known"));
    }

    [Fact]
    public async Task CollisionFoundByTheLookup_NeverConsumesBudget_AndIsNeverCachedAsAnAllow()
    {
        var inner = new StubTenantRegistry
        {
            OnTryGet = (t, _) => ValueTask.FromResult<TenantRegistration?>(t == Acme ? TenantRegistration.Collision : null)
        };
        var gate = Gate(inner, perIssuer: 1);

        for (var i = 0; i < 3; i++)
        {
            (await gate.RegisterOrGetAsync(Acme, "busy", default)).ShouldBe(TenantRegistration.Collision);
        }

        inner.RegisterCalls.ShouldNotContain(c => c.Tenant == Acme);
        inner.TryGetCalls.Count(t => t == Acme).ShouldBe(3);   // a collision is never cached
    }

    [Fact]
    public async Task ExistingFoundByTheLookup_IsCached_PositiveOnly()
    {
        var inner = new StubTenantRegistry
        {
            OnTryGet = (t, _) => ValueTask.FromResult<TenantRegistration?>(t == Acme ? TenantRegistration.Existing : null)
        };
        var gate = Gate(inner);

        await gate.RegisterOrGetAsync(Acme, "iss", default);
        await gate.RegisterOrGetAsync(Acme, "iss", default);

        inner.TryGetCalls.Count(t => t == Acme).ShouldBe(1);
    }

    [Fact]
    public async Task LookupUnavailable_FailsClosed_AndIsNotCached()
    {
        var inner = new StubTenantRegistry
        {
            OnTryGet = (_, _) => throw new InvalidOperationException("down")
        };
        var gate = Gate(inner);

        await Should.ThrowAsync<InvalidOperationException>(async () => await gate.RegisterOrGetAsync(Acme, "iss", default));
        inner.RegisterCalls.ShouldNotContain(c => c.Tenant == Acme);
    }

    [Fact]
    public async Task ExistingAndCollisionResults_DoNotConsumeTheRegistrationBudget()
    {
        var inner = new StubTenantRegistry
        {
            OnRegisterOrGet = (_, _, _) => ValueTask.FromResult(TenantRegistration.Existing)
        };
        var gate = Gate(inner, perIssuer: 1);

        for (var i = 0; i < 5; i++)
        {
            (await gate.RegisterOrGetAsync(new TenantId("t" + i), "iss", default)).ShouldBe(TenantRegistration.Existing);
        }
    }

    [Fact]
    public async Task UnknownAndManyIssuers_ShareBoundedBuckets()
    {
        var inner = new StubTenantRegistry();
        var gate = Gate(inner, perIssuer: 1, maxIssuers: 2);

        (await gate.RegisterOrGetAsync(new TenantId("a1"), "i1", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("a2"), "i2", default)).ShouldBe(TenantRegistration.Registered);
        // beyond the tracked issuers every further issuer shares one overflow bucket
        (await gate.RegisterOrGetAsync(new TenantId("a3"), "i3", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("a4"), "i4", default)).ShouldBe(TenantRegistration.RateLimited);
        (await gate.RegisterOrGetAsync(new TenantId("a5"), null, default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("a6"), null, default)).ShouldBe(TenantRegistration.RateLimited);
    }

    [Fact]
    public async Task CacheIsBounded()
    {
        var inner = new StubTenantRegistry
        {
            OnRegisterOrGet = (_, _, _) => ValueTask.FromResult(TenantRegistration.Existing)
        };
        var gate = Gate(inner, maxCache: 2);

        foreach (var t in new[] { "c1", "c2", "c3" }) await gate.RegisterOrGetAsync(new TenantId(t), "iss", default);
        await gate.RegisterOrGetAsync(new TenantId("c3"), "iss", default); // not cached: asks the store again
        inner.RegisterCalls.Count(c => c.Tenant == new TenantId("c3")).ShouldBe(2);
        await gate.RegisterOrGetAsync(new TenantId("c1"), "iss", default);
        inner.RegisterCalls.Count(c => c.Tenant == new TenantId("c1")).ShouldBe(1);
    }

    [Fact]
    public async Task Preseed_IsForwarded()
    {
        var inner = new StubTenantRegistry();
        var gate = Gate(inner);
        await gate.PreseedAsync(["a"], default);
        inner.PreseedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lookup_IsAnsweredByTheStore_NeverRegistersAndNeverConsumesBudget()
    {
        var inner = new StubTenantRegistry
        {
            OnTryGet = (t, _) => ValueTask.FromResult<TenantRegistration?>(t == Acme ? TenantRegistration.Existing : null)
        };
        var gate = Gate(inner, perIssuer: 0);

        (await gate.TryGetAsync(Acme, default)).ShouldBe(TenantRegistration.Existing);
        (await gate.TryGetAsync(new TenantId("unknown"), default)).ShouldBeNull();
        inner.RegisterCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveCollision_IsForwarded_AndEvictsTheCachedGroup()
    {
        var inner = new StubTenantRegistry
        {
            OnResolveCollision = (t, _, _) => ValueTask.FromResult(new TenantCollisionResolution(TenantCollisionResolutionStatus.Resolved, "acme"))
        };
        var gate = Gate(inner);
        await gate.RegisterOrGetAsync(Acme, "iss", default);   // cached positive

        (await gate.ResolveCollisionAsync(Acme, null, default)).Status.ShouldBe(TenantCollisionResolutionStatus.Resolved);
        await gate.RegisterOrGetAsync(Acme, "iss", default);

        inner.RegisterCalls.Count(c => c.Tenant == Acme).ShouldBe(2);   // the cache was evicted
    }
}
