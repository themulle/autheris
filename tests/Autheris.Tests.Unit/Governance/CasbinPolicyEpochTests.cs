using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class CasbinPolicyEpochTests : IDisposable
{
    private const string User = "S-1-5-21-u1";
    private const string OtherUser = "S-1-5-21-u2";
    private static readonly TenantId Tenant = new("t1");
    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");
    private const string EpochKey = "policy:epoch:t1";
    private const string PolicyEpochGuardChannel = "policy:epoch:changed";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"casbin_epoch_{Guid.NewGuid():N}");
    private readonly string _policyFile;
    private readonly FakeTimeProvider _time = new();
    private readonly SwitchableStateProvider _state = new(new InMemoryClusterStateProvider());
    private readonly CasbinDecisionCacheOptions _options = new() { TtlSeconds = 60, EpochCheckSeconds = 5, UnavailableBackoffMilliseconds = 1000 };
    private readonly List<CasbinEnforcementService> _services = [];

    public CasbinPolicyEpochTests()
    {
        Directory.CreateDirectory(_dir);
        _policyFile = Path.Combine(_dir, "policy.csv");
        WritePolicy("allow");
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        Directory.Delete(_dir, true);
    }

    private void WritePolicy(string effect) =>
        File.WriteAllText(_policyFile, $"p, {User}, {Tenant.Value}, {Table}, read, true, {effect}{Environment.NewLine}");

    private CasbinEnforcementService Node(bool withFile = true, IClusterStateTopology? topology = null, IDistributedClusterStateProvider? state = null)
    {
        var service = new CasbinEnforcementService(
            cacheOptions: _options,
            timeProvider: _time,
            stateProvider: state ?? _state,
            topology: topology ?? new ClusterStateTopology(ClusterStateMode.External));
        _services.Add(service);
        if (withFile)
        {
            service.LoadPolicyFromFile(Tenant, _policyFile);
        }

        return service;
    }

    private static SecurityEvaluationContext Ctx(string user = User) =>
        new(new Sid(user), [], Tenant, Table, [], IPAddress.Loopback, DateTimeOffset.UnixEpoch, "purpose");

    // The epoch grows by a random positive delta per change, so tests wait for "changed" instead of an exact value.
    private async Task<long> WaitForEpochAsync(long previous = 0, string key = EpochKey)
    {
        try
        {
            await WaitHelper.WaitUntil(async () => await _state.Inner.GetAsync<long>(key) != previous, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // fall through: the assertion below reports the unchanged epoch
        }

        var value = await _state.Inner.GetAsync<long>(key);
        value.ShouldNotBe(previous);
        return value;
    }

    [Fact]
    public async Task PolicyChangedOnNodeA_IsVisibleOnNodeB_WithinEpochCheckInterval()
    {
        _state.SuppressEvents = true; // pure polling path: the hint is lost
        var a = Node();
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue(); // caches the allow and initialises the epoch observation

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync();

        _time.Advance(TimeSpan.FromSeconds(1));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue(); // within the check interval the cached decision may still be served

        _time.Advance(TimeSpan.FromSeconds(5));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EpochHint_MakesPeerCheckImmediately_WithoutWaitingForInterval()
    {
        var a = Node();
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync();

        _time.Advance(TimeSpan.FromMilliseconds(10)); // far below EpochCheckSeconds
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EpochHint_ForUnrelatedOrMalformedKey_IsIgnored()
    {
        var b = Node();
        await b.EvaluatePolicyAsync(Ctx());
        var reads = _state.Calls;

        await _state.Inner.PublishEventAsync(PolicyEpochGuardChannel, "not-an-epoch-key");
        await _state.Inner.PublishEventAsync(PolicyEpochGuardChannel, "policy:epoch:other-tenant");
        await b.EvaluatePolicyAsync(Ctx());

        _state.Calls.ShouldBe(reads); // no extra cluster read was triggered
    }

    [Fact]
    public async Task StateUnavailable_WhenCheckDue_BypassesCache_AndEvaluatesFresh()
    {
        var b = Node();
        await b.EvaluatePolicyAsync(Ctx());
        await b.EvaluatePolicyAsync(Ctx());
        b.DiagnosticDecisionCacheHits.ShouldBe(1);

        _state.Unavailable = true;
        _time.Advance(TimeSpan.FromSeconds(6));
        var hitsBefore = b.DiagnosticDecisionCacheHits;
        var countBefore = b.DiagnosticDecisionCacheCount;

        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();

        b.DiagnosticDecisionCacheHits.ShouldBe(hitsBefore);
        b.DiagnosticDecisionCacheCount.ShouldBe(countBefore); // no cache write either
    }

    [Fact]
    public async Task StateUnavailable_NeverServesStaleAllow()
    {
        var a = Node();
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync();

        // B cannot learn about the change while the state is down and evaluates against its own (still allowing) policy; what
        // must hold is that no decision is served from the cache while the epoch is unknown.
        _state.Unavailable = true;
        _time.Advance(TimeSpan.FromSeconds(6));
        var hitsBefore = b.DiagnosticDecisionCacheHits;
        await b.EvaluatePolicyAsync(Ctx());
        await b.EvaluatePolicyAsync(Ctx()); // still inside the backoff: degraded, still no cache
        b.DiagnosticDecisionCacheHits.ShouldBe(hitsBefore);

        _state.Unavailable = false;
        _time.Advance(TimeSpan.FromSeconds(2));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse(); // recovered: the change is picked up
    }

    [Fact]
    public async Task RemoteEpochChange_ReloadDoesNotPropagate()
    {
        var a = Node();
        var b = Node();
        await a.EvaluatePolicyAsync(Ctx());
        await b.EvaluatePolicyAsync(Ctx());

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        var epoch = await WaitForEpochAsync();

        _time.Advance(TimeSpan.FromSeconds(6));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
        (await a.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(30));
        await b.EvaluatePolicyAsync(Ctx());
        await a.EvaluatePolicyAsync(Ctx());
        await Task.Delay(50); // quiet period (50 ms) before a negative assertion: no epoch ping-pong between the nodes

        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBe(epoch); // no ping-pong between the nodes
    }

    [Fact]
    public async Task ClusterEpochReset_LowerValue_TriggersReload()
    {
        await _state.Inner.IncrementAsync(EpochKey, 3, TimeSpan.Zero);
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();

        WritePolicy("deny");
        await _state.Inner.RemoveAsync(EpochKey); // the counter restarts below the observed value
        _time.Advance(TimeSpan.FromSeconds(6));

        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoteReload_DoesNotDropProgrammaticRules()
    {
        var a = Node(withFile: false);
        var b = Node(withFile: false);
        b.AddPolicy(Tenant, OtherUser, Table.ToString(), "read");
        var first = await WaitForEpochAsync();
        (await b.EvaluatePolicyAsync(Ctx(OtherUser))).IsAllowed.ShouldBeTrue();

        a.AddPolicy(Tenant, User, Table.ToString(), "read"); // node-local change on A
        var second = await WaitForEpochAsync(first);
        _time.Advance(TimeSpan.FromSeconds(6));

        (await b.EvaluatePolicyAsync(Ctx(OtherUser))).IsAllowed.ShouldBeTrue(); // reload happened, B's programmatic rule survived
        b.DiagnosticDecisionCacheCount.ShouldBe(1);
        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBe(second); // the reload did not propagate
    }

    [Fact]
    public async Task ConcurrentDueCallers_ReloadOnce()
    {
        var a = Node();
        var b = Node();
        await b.EvaluatePolicyAsync(Ctx());
        var reloads = 0;
        b.OnPolicyReloaded += _ => Interlocked.Increment(ref reloads);

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync();
        _time.Advance(TimeSpan.FromSeconds(6));

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () => (await b.EvaluatePolicyAsync(Ctx())).IsAllowed)));

        decisions.ShouldAllBe(allowed => !allowed);
        reloads.ShouldBe(1);
    }

    private CasbinEnforcementService TenantNode() => Node(withFile: false);

    [Fact]
    public async Task TenantCase_Differs_ChangeOnNodeA_IsSeenByNodeB()
    {
        _state.SuppressEvents = true;
        var a = TenantNode();
        var b = TenantNode();
        var upper = new TenantId("Acme");
        var lower = new TenantId("acme");
        b.AddPolicy(lower, User, Table.ToString(), "read");
        var ctx = new SecurityEvaluationContext(new Sid(User), [], lower, Table, [], IPAddress.Loopback, DateTimeOffset.UnixEpoch, "purpose");
        await b.EvaluatePolicyAsync(ctx);
        await b.EvaluatePolicyAsync(ctx);
        var hits = b.DiagnosticDecisionCacheHits;
        hits.ShouldBe(1);

        a.AddPolicy(upper, OtherUser, Table.ToString(), "read");
        await WaitForEpochAsync(0, "policy:epoch:acme");
        _time.Advance(TimeSpan.FromSeconds(6));

        await b.EvaluatePolicyAsync(ctx);
        b.DiagnosticDecisionCacheHits.ShouldBe(hits); // the change was detected: cache cleared, so a miss
    }

    [Fact]
    public async Task CorruptedEpochValue_IsTreatedAsUnavailable_CacheBypassed()
    {
        var b = Node();
        await b.EvaluatePolicyAsync(Ctx());
        await b.EvaluatePolicyAsync(Ctx());
        var hits = b.DiagnosticDecisionCacheHits;

        await _state.Inner.SetAsync(EpochKey, "garbage", TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(6));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();
        b.DiagnosticDecisionCacheHits.ShouldBe(hits);

        await _state.Inner.RemoveAsync(EpochKey);
        _time.Advance(TimeSpan.FromSeconds(2));
        await b.EvaluatePolicyAsync(Ctx()); // recovered after the backoff
        await b.EvaluatePolicyAsync(Ctx());
        b.DiagnosticDecisionCacheHits.ShouldBeGreaterThan(hits);
    }

    [Fact]
    public async Task ReloadFailure_IsRetriedOnNextCheck()
    {
        _state.SuppressEvents = true;
        var a = Node();
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue();

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync();
        var backup = File.ReadAllText(_policyFile);
        File.Delete(_policyFile); // B cannot reload now

        _time.Advance(TimeSpan.FromSeconds(6));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue(); // last-known-good stays active

        File.WriteAllText(_policyFile, backup);
        _time.Advance(TimeSpan.FromSeconds(6));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse(); // the change is still pending, so it is retried
    }

    [Fact]
    public async Task WildcardChange_IsDetectedThroughAnotherTenantsCheck()
    {
        _state.SuppressEvents = true;
        var a = TenantNode();
        var b = TenantNode();
        b.AddPolicy(Tenant, User, Table.ToString(), "read");
        await b.EvaluatePolicyAsync(Ctx());
        await b.EvaluatePolicyAsync(Ctx());
        var hits = b.DiagnosticDecisionCacheHits;

        a.LoadPolicyFromText($"p, {OtherUser}, {Tenant.Value}, {Table}, read, true, allow"); // global policy: wildcard epoch
        await WaitForEpochAsync(0, "policy:epoch:*");
        _time.Advance(TimeSpan.FromSeconds(6));

        await b.EvaluatePolicyAsync(Ctx());
        b.DiagnosticDecisionCacheHits.ShouldBe(hits);
    }

    [Fact]
    public async Task Dispose_UnsubscribesHintChannel()
    {
        var node = Node();
        _state.ActiveSubscriptions.ShouldBe(1);

        node.Dispose();

        try
        {
            await WaitHelper.WaitUntil(() => _state.ActiveSubscriptions == 0, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // fall through: the assertion below reports the remaining subscriptions
        }

        _state.ActiveSubscriptions.ShouldBe(0);
    }

    [Fact]
    public async Task OwnChanges_UseRandomPositiveDelta()
    {
        var node = Node(withFile: false);
        for (var i = 0; i < 20; i++)
        {
            node.AddPolicy(Tenant, $"S-1-5-21-x{i}", Table.ToString(), "read");
        }

        await Task.Delay(100); // quiet period (100 ms): lets the asynchronous epoch bumps land (no completion signal)
        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBeGreaterThan(20); // 20 increments of exactly 1 would give 20
    }

    [Fact]
    public async Task Guard_TrackedTenants_AreEvictedWhenIdle_NotCappedForever()
    {
        using var guard = new Autheris.Application.Governance.Caching.PolicyEpochGuard(
            _state, _time, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
            maxTrackedTenants: 3, idleEvictionAfter: TimeSpan.FromSeconds(10));
        Func<bool, bool, bool> noop = (_, _) => true;

        foreach (var t in new[] { "t1", "t2", "t3" })
        {
            (await guard.CheckAsync(t, noop, default)).Kind.ShouldBe(Autheris.Application.Governance.Caching.EpochCheckKind.Unchanged);
        }

        // At the cap and nothing idle: the fourth tenant is answered as unavailable (evaluated uncached), nothing is tracked.
        (await guard.CheckAsync("t4", noop, default)).Kind.ShouldBe(Autheris.Application.Governance.Caching.EpochCheckKind.Unavailable);
        guard.TrackedTenantCount.ShouldBe(3);

        _time.Advance(TimeSpan.FromSeconds(11));
        (await guard.CheckAsync("t4", noop, default)).Kind.ShouldBe(Autheris.Application.Governance.Caching.EpochCheckKind.Unchanged);
        guard.TrackedTenantCount.ShouldBeLessThanOrEqualTo(3);
        guard.GetStatus("t4").ShouldBe(Autheris.Application.Governance.Caching.EpochStatus.Fresh);
    }

    [Fact]
    public async Task SingleNodeTopology_NeverReadsClusterState()
    {
        var node = Node(topology: new ClusterStateTopology(ClusterStateMode.InMemory));
        await node.EvaluatePolicyAsync(Ctx());
        _time.Advance(TimeSpan.FromMinutes(10));
        node.AddPolicy(Tenant, OtherUser, Table.ToString(), "read");
        await node.EvaluatePolicyAsync(Ctx());

        _state.Calls.ShouldBe(0);
    }

    [Fact]
    public void ClusterTopology_WithoutStateProvider_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentNullException>(() => new CasbinEnforcementService(
            topology: new ClusterStateTopology(ClusterStateMode.External)));
    }
}

/// <summary>Delegating cluster state that can simulate an outage and counts calls.</summary>
internal sealed class SwitchableStateProvider(InMemoryClusterStateProvider inner) : IDistributedClusterStateProvider
{
    private int _calls;

    public InMemoryClusterStateProvider Inner { get; } = inner;

    public volatile bool Unavailable;

    public int Calls => Volatile.Read(ref _calls);

    private void Guard()
    {
        Interlocked.Increment(ref _calls);
        if (Unavailable)
        {
            throw new ClusterStateUnavailableException();
        }
    }

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        Guard();
        return Inner.GetAsync<T>(key, ct);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        Guard();
        return Inner.SetAsync(key, value, ttl, ct);
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        Guard();
        return Inner.RemoveAsync(key, ct);
    }

    public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return Unavailable ? ValueTask.FromResult<long?>(null) : Inner.IncrementAsync(key, delta, ttl, ct);
    }

    public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
    {
        Guard();
        return Inner.TryConsumeBudgetAsync(key, cost, limit, ttl, ct);
    }

    public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default)
    {
        Guard();
        if (SuppressEvents)
        {
            return ValueTask.CompletedTask;
        }

        return Inner.PublishEventAsync(channel, payload, ct);
    }

    public volatile bool SuppressEvents;

    private int _subscriptions;

    public int ActiveSubscriptions => Volatile.Read(ref _subscriptions);

    public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default)
    {
        var inner = Inner.SubscribeAsync(channel, handler, ct);
        Interlocked.Increment(ref _subscriptions);
        return new Subscription(inner, () => Interlocked.Decrement(ref _subscriptions));
    }

    private sealed class Subscription(IAsyncDisposable inner, Action onDispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            onDispose();
        }
    }

    public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default)
    {
        Guard();
        return Inner.TryAcquireLockAsync(resourceKey, expiry, ct);
    }

    public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default)
    {
        Guard();
        return Inner.TryAcquireLeaseAsync(resourceKey, ttl, ct);
    }

    public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default)
    {
        Guard();
        return Inner.TryAcquireSlotAsync(setKey, member, limit, ttl, ct);
    }

    public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default)
    {
        Guard();
        return Inner.RenewSlotAsync(setKey, member, ttl, ct);
    }

    public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default)
    {
        Guard();
        return Inner.ReleaseSlotAsync(setKey, member, ct);
    }
}
