namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-X1-13: positive-only cache, no cached allow for collisions or outages, per-issuer budget for new registrations.</summary>
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
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Acme, "iss", Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Registered));
        var gate = Gate(inner, clock);

        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Existing);
        await inner.Received(1).RegisterOrGetAsync(Acme, "iss", Arg.Any<CancellationToken>());

        clock.Advance(TimeSpan.FromSeconds(31));
        await gate.RegisterOrGetAsync(Acme, "iss", default);
        await inner.Received(2).RegisterOrGetAsync(Acme, "iss", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Collision_IsNeverCached_AndEvictsTheCachedSiblingSpelling()
    {
        var inner = Substitute.For<ITenantRegistry>();
        var lower = new TenantId("acme");
        var upper = new TenantId("ACME");
        inner.RegisterOrGetAsync(lower, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            new ValueTask<TenantRegistration>(TenantRegistration.Registered), new ValueTask<TenantRegistration>(TenantRegistration.Collision));
        inner.RegisterOrGetAsync(upper, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Collision));
        var gate = Gate(inner, perIssuer: 10);

        (await gate.RegisterOrGetAsync(lower, "iss", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(upper, "iss", default)).ShouldBe(TenantRegistration.Collision);
        // the cached allow for the first spelling is gone: the store is asked again and denies
        (await gate.RegisterOrGetAsync(lower, "iss", default)).ShouldBe(TenantRegistration.Collision);
        (await gate.RegisterOrGetAsync(upper, "iss", default)).ShouldBe(TenantRegistration.Collision);
        await inner.Received(2).RegisterOrGetAsync(upper, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegistryUnavailable_NotCachedAsAllow()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new InvalidOperationException("down"),
            _ => new ValueTask<TenantRegistration>(TenantRegistration.Collision));
        var gate = Gate(inner);

        await Should.ThrowAsync<InvalidOperationException>(async () => await gate.RegisterOrGetAsync(Acme, "iss", default));
        (await gate.RegisterOrGetAsync(Acme, "iss", default)).ShouldBe(TenantRegistration.Collision);
        await inner.Received(2).RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegistrationRateLimit_PerIssuer()
    {
        var clock = new ManualClock();
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Registered));
        var gate = Gate(inner, clock, perIssuer: 2);

        (await gate.RegisterOrGetAsync(new TenantId("t1"), "evil", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("t2"), "evil", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("t3"), "evil", default)).ShouldBe(TenantRegistration.RateLimited);
        await inner.DidNotReceive().RegisterOrGetAsync(new TenantId("t3"), Arg.Any<string?>(), Arg.Any<CancellationToken>());

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
        var inner = Substitute.For<ITenantRegistry>();
        inner.TryGetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>((TenantRegistration?)null));
        inner.TryGetAsync(new TenantId("known"), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>(TenantRegistration.Existing));
        inner.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Registered));
        var gate = Gate(inner, perIssuer: 1);

        (await gate.RegisterOrGetAsync(new TenantId("new1"), "busy", default)).ShouldBe(TenantRegistration.Registered);
        (await gate.RegisterOrGetAsync(new TenantId("new2"), "busy", default)).ShouldBe(TenantRegistration.RateLimited);   // the budget is gone

        // a tenant that merely exists (another node registered it, or the cache expired) is a lookup, not a registration
        (await gate.RegisterOrGetAsync(new TenantId("known"), "busy", default)).ShouldBe(TenantRegistration.Existing);
        await inner.DidNotReceive().RegisterOrGetAsync(new TenantId("known"), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollisionFoundByTheLookup_NeverConsumesBudget_AndIsNeverCachedAsAnAllow()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.TryGetAsync(Acme, Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>(TenantRegistration.Collision));
        var gate = Gate(inner, perIssuer: 1);

        for (var i = 0; i < 3; i++)
        {
            (await gate.RegisterOrGetAsync(Acme, "busy", default)).ShouldBe(TenantRegistration.Collision);
        }

        await inner.DidNotReceive().RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await inner.Received(3).TryGetAsync(Acme, Arg.Any<CancellationToken>());   // a collision is never cached
    }

    [Fact]
    public async Task ExistingFoundByTheLookup_IsCached_PositiveOnly()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.TryGetAsync(Acme, Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>(TenantRegistration.Existing));
        var gate = Gate(inner);

        await gate.RegisterOrGetAsync(Acme, "iss", default);
        await gate.RegisterOrGetAsync(Acme, "iss", default);

        await inner.Received(1).TryGetAsync(Acme, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LookupUnavailable_FailsClosed_AndIsNotCached()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.TryGetAsync(Acme, Arg.Any<CancellationToken>()).Returns<ValueTask<TenantRegistration?>>(_ => throw new InvalidOperationException("down"));
        var gate = Gate(inner);

        await Should.ThrowAsync<InvalidOperationException>(async () => await gate.RegisterOrGetAsync(Acme, "iss", default));
        await inner.DidNotReceive().RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingAndCollisionResults_DoNotConsumeTheRegistrationBudget()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Existing));
        var gate = Gate(inner, perIssuer: 1);

        for (var i = 0; i < 5; i++)
        {
            (await gate.RegisterOrGetAsync(new TenantId("t" + i), "iss", default)).ShouldBe(TenantRegistration.Existing);
        }
    }

    [Fact]
    public async Task UnknownAndManyIssuers_ShareBoundedBuckets()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Registered));
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
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Existing));
        var gate = Gate(inner, maxCache: 2);

        foreach (var t in new[] { "c1", "c2", "c3" }) await gate.RegisterOrGetAsync(new TenantId(t), "iss", default);
        await gate.RegisterOrGetAsync(new TenantId("c3"), "iss", default); // not cached: asks the store again
        await inner.Received(2).RegisterOrGetAsync(new TenantId("c3"), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await gate.RegisterOrGetAsync(new TenantId("c1"), "iss", default);
        await inner.Received(1).RegisterOrGetAsync(new TenantId("c1"), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preseed_IsForwarded()
    {
        var inner = Substitute.For<ITenantRegistry>();
        var gate = Gate(inner);
        await gate.PreseedAsync(["a"], default);
        await inner.Received(1).PreseedAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lookup_IsAnsweredByTheStore_NeverRegistersAndNeverConsumesBudget()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.TryGetAsync(Acme, Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration?>(TenantRegistration.Existing));
        var gate = Gate(inner, perIssuer: 0);

        (await gate.TryGetAsync(Acme, default)).ShouldBe(TenantRegistration.Existing);
        (await gate.TryGetAsync(new TenantId("unknown"), default)).ShouldBeNull();
        await inner.DidNotReceiveWithAnyArgs().RegisterOrGetAsync(default, default, default);
    }

    [Fact]
    public async Task ResolveCollision_IsForwarded_AndEvictsTheCachedGroup()
    {
        var inner = Substitute.For<ITenantRegistry>();
        inner.RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<TenantRegistration>(TenantRegistration.Registered));
        inner.ResolveCollisionAsync(Acme, Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var gate = Gate(inner);
        await gate.RegisterOrGetAsync(Acme, "iss", default);   // cached positive

        (await gate.ResolveCollisionAsync(Acme, default)).ShouldBeTrue();
        await gate.RegisterOrGetAsync(Acme, "iss", default);

        await inner.Received(2).RegisterOrGetAsync(Acme, Arg.Any<string?>(), Arg.Any<CancellationToken>());   // the cache was evicted
    }
}
