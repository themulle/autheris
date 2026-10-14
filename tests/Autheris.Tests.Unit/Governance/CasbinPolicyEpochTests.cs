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

    private async Task WaitForEpochAsync(long expected)
    {
        for (var i = 0; i < 200 && await _state.Inner.GetAsync<long>(EpochKey) != expected; i++)
        {
            await Task.Delay(10);
        }

        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBe(expected);
    }

    [Fact]
    public async Task PolicyChangedOnNodeA_IsVisibleOnNodeB_WithinEpochCheckInterval()
    {
        var a = Node();
        var b = Node();
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue(); // caches the allow and initialises the epoch observation

        WritePolicy("deny");
        await a.ReloadPoliciesAsync(Tenant);
        await WaitForEpochAsync(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeTrue(); // within the check interval the cached decision may still be served

        _time.Advance(TimeSpan.FromSeconds(5));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
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
        await WaitForEpochAsync(1);

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
        await WaitForEpochAsync(1);

        _time.Advance(TimeSpan.FromSeconds(6));
        (await b.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
        (await a.EvaluatePolicyAsync(Ctx())).IsAllowed.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(30));
        await b.EvaluatePolicyAsync(Ctx());
        await a.EvaluatePolicyAsync(Ctx());
        await Task.Delay(50);

        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBe(1); // no ping-pong between the nodes
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
        b.AddPolicy(Tenant, OtherUser, Table.ToString(), "read"); // epoch 1
        await WaitForEpochAsync(1);
        (await b.EvaluatePolicyAsync(Ctx(OtherUser))).IsAllowed.ShouldBeTrue();

        a.AddPolicy(Tenant, User, Table.ToString(), "read"); // node-local change on A: epoch 2
        await WaitForEpochAsync(2);
        _time.Advance(TimeSpan.FromSeconds(6));

        (await b.EvaluatePolicyAsync(Ctx(OtherUser))).IsAllowed.ShouldBeTrue(); // reload happened, B's programmatic rule survived
        b.DiagnosticDecisionCacheCount.ShouldBe(1);
        (await _state.Inner.GetAsync<long>(EpochKey)).ShouldBe(2);
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
        await WaitForEpochAsync(1);
        _time.Advance(TimeSpan.FromSeconds(6));

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () => (await b.EvaluatePolicyAsync(Ctx())).IsAllowed)));

        decisions.ShouldAllBe(allowed => !allowed);
        reloads.ShouldBe(1);
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
        return Inner.PublishEventAsync(channel, payload, ct);
    }

    public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) =>
        Inner.SubscribeAsync(channel, handler, ct);

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
}
