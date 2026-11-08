using Autheris.Api.Configuration.Validation;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class GroupResolutionCacheTests
{
    private const string SidA = "S-1-5-21-1-2-3-1105";
    private const string SidB = "S-1-5-21-1-2-3-1106";

    private static IdentityRealm Realm(string id, IdentityRealmKind kind = IdentityRealmKind.Customer) => new(
        id, kind, TenantBindingSpecification.Fixed(new TenantId("t")), RealmGroupSource.Resolver, RealmProvisioningMode.Scim, resolverChain: ["p"]);

    private static ResolvedIdentity Identity(string realmId, string userKey = "user-1") =>
        new(userKey, null, null, null, new TenantId("t"), realmId, []);

    private static IOptions<GatewayOptions> Options(int ttl) =>
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = ttl } });

    private static IRoleProvider Provider(Func<IdentityRealm, ProviderRoleResult> result)
    {
        var provider = Substitute.For<IRoleProvider>();
        provider.ProviderId.Returns("p");
        provider.GetRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<IdentityRealm>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(result(call.Arg<IdentityRealm>())));
        return provider;
    }

    private static ProviderRoleResult Groups(params string[] groups) =>
        new(new HashSet<string>(groups), new HashSet<string>(), new Dictionary<string, string>());

    private static RoleResolution Entry(TimeSpan lifetime) => RoleResolution.EmptyWithExpiry(DateTimeOffset.UtcNow.Add(lifetime));

    private static Task<bool> EventuallyGoneAsync(RoleResolverCache cache, string realm, string user) =>
        Eventually.WaitAsync(() => cache.GetAsync(realm, user).AsTask().GetAwaiter().GetResult() is null, TimeSpan.FromSeconds(5));

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07a")]
    public async Task Entry_expires_after_configured_ttl()
    {
        var cache = new RoleResolverCache();
        await cache.SetAsync("r", "u", Entry(TimeSpan.FromMilliseconds(60)));
        (await cache.GetAsync("r", "u")).ShouldNotBeNull();

        (await EventuallyGoneAsync(cache, "r", "u")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(IdentityRealmKind.EntraWorkforce, 3)]
    [InlineData(IdentityRealmKind.Customer, 7)]
    [Trait("SecurityCondition", "SEC-AUTH-07a")]
    public async Task Workforce_realm_uses_configured_ttl_not_fifteen_minutes(IdentityRealmKind kind, int ttl)
    {
        var sut = new RoleResolver([Provider(_ => Groups(SidA))], new RoleResolverCache(), null, Options(ttl));

        var resolution = await sut.ResolveAsync(Identity("r"), Realm("r", kind));

        (resolution.ExpiresAt - resolution.ResolvedAt).ShouldBe(TimeSpan.FromMinutes(ttl), TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(-5)]
    [Trait("SecurityCondition", "SEC-AUTH-07a")]
    public void Ttl_outside_1_to_60_fails_startup(int ttl)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        var validator = new AuthenticationModeOptionsValidator(env);

        var result = validator.Validate(null, new GatewayOptions { Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = ttl } });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("GroupCacheTtlMinutes");
        validator.Validate(null, new GatewayOptions { Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 5 } }).Failed.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Scim_membership_change_invalidates_on_all_nodes()
    {
        await using var bus = new InProcessChannelEventBus();
        var nodeA = new RoleResolverCache();
        var nodeB = new RoleResolverCache();
        var publisher = new BroadcastingRoleResolverCache(nodeA, bus);
        using var subscriberB = new IdentityGroupsChangedSubscriber(nodeB, bus);
        await subscriberB.StartAsync(CancellationToken.None);
        await nodeA.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));
        await nodeB.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));
        await nodeB.SetAsync("r", "other", Entry(TimeSpan.FromMinutes(5)));

        publisher.Invalidate("r", "u");

        (await EventuallyGoneAsync(nodeB, "r", "u")).ShouldBeTrue();
        (await nodeA.GetAsync("r", "u")).ShouldBeNull();
        (await nodeB.GetAsync("r", "other")).ShouldNotBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Lost_event_falls_back_to_ttl()
    {
        var nodeB = new RoleResolverCache();
        await nodeB.SetAsync("r", "u", Entry(TimeSpan.FromMilliseconds(60)));

        // No event is delivered: the entry stays until its TTL ends, then it is gone.
        (await nodeB.GetAsync("r", "u")).ShouldNotBeNull();
        (await EventuallyGoneAsync(nodeB, "r", "u")).ShouldBeTrue();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Event_handler_failure_invalidates_all()
    {
        var node = new RoleResolverCache();
        using var subscriber = new IdentityGroupsChangedSubscriber(node, Substitute.For<Autheris.Application.Common.Interfaces.IEventBus>());
        await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));

        // A message that cannot be interpreted (missing key list, null message) drops every entry.
        await subscriber.Handle(new IdentityGroupsChanged("r", null!, false));
        (await node.GetAsync("r", "u")).ShouldBeNull();

        await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));
        await subscriber.Handle(null);
        (await node.GetAsync("r", "u")).ShouldBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Connection_restored_invalidates_all()
    {
        await using var bus = new InProcessChannelEventBus();
        var node = new RoleResolverCache();
        using var subscriber = new IdentityGroupsChangedSubscriber(node, bus);
        await subscriber.StartAsync(CancellationToken.None);
        await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));

        bus.SimulateConnectionRestored();

        (await node.GetAsync("r", "u")).ShouldBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Oversized_invalidation_message_degrades_to_invalidate_all()
    {
        var node = new RoleResolverCache();
        using var subscriber = new IdentityGroupsChangedSubscriber(node, Substitute.For<Autheris.Application.Common.Interfaces.IEventBus>());
        await node.SetAsync("r", "unrelated", Entry(TimeSpan.FromMinutes(5)));
        var tooMany = Enumerable.Range(0, IdentityGroupsChangedChannel.MaxUserKeys + 1).Select(i => $"u{i}").ToArray();

        await subscriber.Handle(new IdentityGroupsChanged("r", tooMany, false));

        (await node.GetAsync("r", "unrelated")).ShouldBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07c")]
    public async Task Same_userkey_in_two_realms_does_not_share_cache_entries()
    {
        var sut = new RoleResolver([Provider(realm => realm.Id == "realm-a" ? Groups(SidA) : Groups(SidB))], new RoleResolverCache(), null, Options(5));

        var a = await sut.ResolveAsync(Identity("realm-a", "same-user"), Realm("realm-a"));
        var b = await sut.ResolveAsync(Identity("realm-b", "same-user"), Realm("realm-b"));
        var aAgain = await sut.ResolveAsync(Identity("realm-a", "same-user"), Realm("realm-a"));

        a.Groups.ShouldBe([SidA]);
        b.Groups.ShouldBe([SidB]);
        aAgain.Groups.ShouldBe([SidA]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07c")]
    public async Task Overlong_keys_are_never_cached()
    {
        var cache = new RoleResolverCache();
        var longKey = new string('k', IdentityGroupsChangedChannel.MaxKeyLength + 1);

        await cache.SetAsync("r", longKey, Entry(TimeSpan.FromMinutes(5)));

        (await cache.GetAsync("r", longKey)).ShouldBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03h")]
    public async Task Degraded_resolution_is_never_cached()
    {
        var cache = new RoleResolverCache();
        var fail = true;
        var sut = new RoleResolver([Provider(_ => fail ? ProviderRoleResult.FailedResult : Groups(SidA))], cache, null, Options(5));

        var first = await sut.ResolveAsync(Identity("r"), Realm("r"));
        first.IsDegraded.ShouldBeTrue();
        (await cache.GetAsync("r", "user-1")).ShouldBeNull();

        await cache.SetAsync("r", "user-2", first);
        (await cache.GetAsync("r", "user-2")).ShouldBeNull();

        fail = false;
        (await sut.ResolveAsync(Identity("r"), Realm("r"))).Groups.ShouldBe([SidA]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Undeliverable_message_through_redis_bus_invalidates_all()
    {
        var redisSubscriber = Substitute.For<StackExchange.Redis.ISubscriber>();
        Action<StackExchange.Redis.RedisChannel, StackExchange.Redis.RedisValue>? onMessage = null;
        redisSubscriber.When(x => x.Subscribe(Arg.Any<StackExchange.Redis.RedisChannel>(), Arg.Any<Action<StackExchange.Redis.RedisChannel, StackExchange.Redis.RedisValue>>(), Arg.Any<StackExchange.Redis.CommandFlags>()))
            .Do(call => onMessage = call.Arg<Action<StackExchange.Redis.RedisChannel, StackExchange.Redis.RedisValue>>());
        var mux = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
        mux.GetSubscriber(Arg.Any<object?>()).Returns(redisSubscriber);
        using var bus = new RedisEventBus(mux, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisEventBus>.Instance);
        var node = new RoleResolverCache();
        using var subscriber = new IdentityGroupsChangedSubscriber(node, bus);
        await subscriber.StartAsync(CancellationToken.None);
        onMessage.ShouldNotBeNull();

        foreach (var payload in new[] { "{ not json", "null" })
        {
            await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));
            onMessage!(default, payload);
            (await EventuallyGoneAsync(node, "r", "u")).ShouldBeTrue();
        }
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Subscribe_failure_invalidates_all_and_still_hooks_connection_restored()
    {
        var bus = Substitute.For<Autheris.Application.Common.Interfaces.IEventBus>();
        bus.Subscribe<IdentityGroupsChanged>(Arg.Any<string>(), Arg.Any<Func<IdentityGroupsChanged, Task>>()).Returns(_ => throw new InvalidOperationException("down"));
        var node = new RoleResolverCache();
        using var subscriber = new IdentityGroupsChangedSubscriber(node, bus);
        await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));

        await subscriber.StartAsync(CancellationToken.None);

        (await node.GetAsync("r", "u")).ShouldBeNull();
        await node.SetAsync("r", "u", Entry(TimeSpan.FromMinutes(5)));
        bus.ConnectionRestored += Raise.Event<Action>();
        (await node.GetAsync("r", "u")).ShouldBeNull();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-07b")]
    public async Task Publish_failure_is_counted_without_keys_and_does_not_throw()
    {
        var bus = Substitute.For<Autheris.Application.Common.Interfaces.IEventBus>();
        bus.PublishAsync(Arg.Any<string>(), Arg.Any<IdentityGroupsChanged>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("down"));
        var tags = new List<string>();
        long total = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == "autheris_identity_group_invalidation_publish_failed_total") l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, v, t, _) =>
        {
            lock (tags) { total += v; foreach (var tag in t) tags.Add(tag.Key); }
        });
        listener.Start();
        var cache = new BroadcastingRoleResolverCache(new RoleResolverCache(), bus);

        cache.Invalidate("r", "secret-user-key");

        (await Eventually.WaitAsync(() => { lock (tags) { return total > 0; } }, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        lock (tags) { tags.ShouldBeEmpty(); }
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-03h")]
    public async Task Unknown_realm_resolution_is_degraded()
    {
        var sut = new RoleResolver([Provider(_ => Groups(SidA))], new RoleResolverCache(), null, Options(5));

        var resolution = await sut.ResolveRolesAsync(Identity("no-such-realm"));

        resolution.IsDegraded.ShouldBeTrue();
        resolution.Groups.ShouldBeEmpty();
    }
}
