namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.ResultCache;
using Shouldly;
using Xunit;

public sealed class WebSqlResultCacheTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static async Task<bool> StoreAsync(CacheHarness h, WebSqlResultCacheKey key, CachedWebSqlResult? result = null, TimeSpan? ttl = null)
    {
        using var fill = await h.Cache.LookupAsync(key, false, Wait, default);
        fill.IsFillLeader.ShouldBeTrue();
        return await h.Cache.TryCommitAsync(fill, result ?? ResultCacheFixtures.Result(), ttl ?? Ttl, "default", "trace-1", default);
    }

    private static async Task<WebSqlResultCacheLookup> LookupAsync(CacheHarness h, WebSqlResultCacheKey key, bool bypass = false) =>
        await h.Cache.LookupAsync(key, bypass, Wait, default);

    [Fact]
    public async Task Single_node_without_redis_stores_and_hits()
    {
        using var h = new CacheHarness();
        h.Epochs.Cluster = 0;

        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Time.Advance(TimeSpan.FromSeconds(7));
        using var hit = await LookupAsync(h, CacheHarness.Key());

        hit.Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        hit.Hit.ShouldNotBeNull().AgeSeconds.ShouldBe(7);
        hit.Hit.StoredTraceId.ShouldBe("trace-1");
        hit.Hit.Result.Rows.Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("table_epochs")]
    [InlineData("cluster")]
    [InlineData("epoch_throws")]
    [InlineData("governance_throws")]
    [Trait("Category", "Security")]
    public async Task Null_or_throwing_epoch_gives_epoch_unavailable_no_store_SECRC07(string failure)
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        switch (failure)
        {
            case "generation": h.Epochs.Generation = null; break;
            case "table_epochs": h.Epochs.Epochs = null; break;
            case "cluster": h.Epochs.Cluster = null; break;
            case "epoch_throws": h.Epochs.ThrowOnRead = true; break;
            default:
                h.Store.Throw = true;
                h.Governance.InvalidateAll();
                break;
        }

        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        lookup.Reason.ShouldBe(WebSqlResultCacheReasons.EpochUnavailable);
        lookup.IsFillLeader.ShouldBeFalse();
        lookup.Hit.ShouldBeNull();
        (await h.Cache.TryCommitAsync(lookup, ResultCacheFixtures.Result(), Ttl, "default", null, default)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Missing_epoch_service_never_caches_SECRC08()
    {
        using var h = new CacheHarness(withEpochServices: false);

        h.Cache.IsEnabled.ShouldBeFalse();
        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        lookup.Reason.ShouldBe(WebSqlResultCacheReasons.EpochUnavailable);
        lookup.IsFillLeader.ShouldBeFalse();
        h.Cache.TryReserve(CacheHarness.Key()).ShouldBeNull();
    }

    [Fact]
    public async Task Gate_zero_never_hits_and_never_stores()
    {
        using var h = new CacheHarness(megabytes: 0);

        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        lookup.Reason.ShouldBe(WebSqlResultCacheReasons.SourceDisabled);
        h.Cache.IsEnabled.ShouldBeFalse();
        h.Cache.Limits.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Closing_the_gate_at_runtime_clears_entries()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Cache.EntryCount.ShouldBe(1);

        h.Options.SetCurrent(CacheHarness.Create(0));
        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        h.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Closing_the_gate_clears_the_store_without_any_request_SECRC08()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Cache.EntryCount.ShouldBe(1);

        h.Options.SetCurrent(CacheHarness.Create(0));

        h.Cache.EntryCount.ShouldBe(0);
        h.Cache.Limits.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Limit_changes_apply_on_reload_and_the_limits_are_cached()
    {
        using var h = new CacheHarness(maxUserMegabytes: 8);
        var before = h.Cache.Limits;
        ReferenceEquals(before, h.Cache.Limits).ShouldBeTrue();

        h.Options.SetCurrent(CacheHarness.Create(16, maxUserMegabytes: 2));

        h.Cache.Limits.MaxUserBytes.ShouldBe(2L * 1024 * 1024);
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Table_epoch_and_governance_epoch_bump_miss_SECRC06()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.BumpGovernance();

        using (var afterGovernance = await LookupAsync(h, CacheHarness.Key()))
        {
            afterGovernance.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
            afterGovernance.IsFillLeader.ShouldBeTrue();
        }

        h.Cache.EntryCount.ShouldBe(0);

        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Epochs.Epochs![CacheHarness.Orders] = 2;
        using var afterTable = await LookupAsync(h, CacheHarness.Key());

        afterTable.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        h.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Table_in_invalidation_log_removes_the_entry()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Epochs.Generation = 2;
        h.Epochs.Invalidated = new HashSet<string> { CacheHarness.Orders.ToString().ToLowerInvariant() };

        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        h.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Unrelated_table_in_invalidation_log_keeps_the_entry()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Epochs.Generation = 2;
        h.Epochs.Invalidated = new HashSet<string> { "main.dbo.other" };

        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Hit);
    }

    [Fact]
    public async Task Unprovable_log_or_missed_cluster_announcement_removes_the_entry()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Epochs.Invalidated = null;
        using (var noLog = await LookupAsync(h, CacheHarness.Key()))
        {
            noLog.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        }

        h.Epochs.Invalidated = new HashSet<string>();
        (await StoreAsync(h, CacheHarness.Key())).ShouldBeTrue();
        h.Epochs.Generation = 2;
        h.Epochs.Cluster = 3;

        using var missed = await LookupAsync(h, CacheHarness.Key());

        missed.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        h.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Epoch_change_during_execution_no_store_SECRC07()
    {
        using var h = new CacheHarness();
        using var fill = await LookupAsync(h, CacheHarness.Key());

        h.BumpGovernance();
        var stored = await h.Cache.TryCommitAsync(fill, ResultCacheFixtures.Result(), Ttl, "default", null, default);

        stored.ShouldBeFalse();
        fill.Reason.ShouldBe(WebSqlResultCacheReasons.GovernanceChanged);
        h.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Commit_after_table_invalidation_during_execution_does_not_store()
    {
        using var h = new CacheHarness();
        using var fill = await LookupAsync(h, CacheHarness.Key());

        h.Epochs.Generation = 2;
        h.Epochs.Invalidated = new HashSet<string> { CacheHarness.Orders.ToString().ToLowerInvariant() };

        (await h.Cache.TryCommitAsync(fill, ResultCacheFixtures.Result(), Ttl, "default", null, default)).ShouldBeFalse();
        fill.Reason.ShouldBe(WebSqlResultCacheReasons.GovernanceChanged);
    }

    [Fact]
    public async Task Concurrent_lookups_yield_one_leader_and_followers_hit_after_the_commit()
    {
        using var h = new CacheHarness();
        using var leader = await LookupAsync(h, CacheHarness.Key());
        var followers = Enumerable.Range(0, 6).Select(_ => LookupAsync(h, CacheHarness.Key())).ToArray();
        await Task.Delay(50);
        followers.ShouldAllBe(t => !t.IsCompleted);

        (await h.Cache.TryCommitAsync(leader, ResultCacheFixtures.Result(), Ttl, "default", null, default)).ShouldBeTrue();
        leader.Dispose();
        var results = await Task.WhenAll(followers);

        leader.IsFillLeader.ShouldBeTrue();
        results.ShouldAllBe(r => r.Status == WebSqlResultCacheStatus.Hit && !r.IsFillLeader);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Leader_cancelled_followers_execute_independently_SECRC15()
    {
        using var h = new CacheHarness();
        var leader = await LookupAsync(h, CacheHarness.Key());
        var follower = LookupAsync(h, CacheHarness.Key());
        await Task.Delay(50);

        leader.Dispose();
        using var result = await follower;

        result.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        result.IsFillLeader.ShouldBeFalse();
        result.Hit.ShouldBeNull();
        using var next = await LookupAsync(h, CacheHarness.Key());
        next.IsFillLeader.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Follower_revalidates_epochs_after_leader_SECRC15()
    {
        using var h = new CacheHarness();
        using var leader = await LookupAsync(h, CacheHarness.Key());
        var follower = LookupAsync(h, CacheHarness.Key());
        await Task.Delay(50);
        (await h.Cache.TryCommitAsync(leader, ResultCacheFixtures.Result(), Ttl, "default", null, default)).ShouldBeTrue();

        h.BumpGovernance();
        leader.Dispose();
        using var result = await follower;

        result.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        result.Hit.ShouldBeNull();
        result.IsFillLeader.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Followers_of_different_sid_never_share_flight_SECRC15()
    {
        using var h = new CacheHarness();
        using var first = await LookupAsync(h, CacheHarness.Key("h-a", "S-1"));

        using var second = await LookupAsync(h, CacheHarness.Key("h-b", "S-2"));

        first.IsFillLeader.ShouldBeTrue();
        second.IsFillLeader.ShouldBeTrue();
    }

    [Fact]
    public async Task Follower_times_out_and_executes_without_a_lease()
    {
        using var h = new CacheHarness();
        using var leader = await LookupAsync(h, CacheHarness.Key());

        using var follower = await h.Cache.LookupAsync(CacheHarness.Key(), false, TimeSpan.FromMilliseconds(30), default);

        follower.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        follower.IsFillLeader.ShouldBeFalse();
    }

    [Fact]
    public async Task Follower_cancellation_propagates_only_to_that_follower()
    {
        using var h = new CacheHarness();
        using var leader = await LookupAsync(h, CacheHarness.Key());
        using var cts = new CancellationTokenSource();
        var follower = h.Cache.LookupAsync(CacheHarness.Key(), false, Wait, cts.Token).AsTask();
        await Task.Delay(30);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(follower);
        leader.IsFillLeader.ShouldBeTrue();
    }

    [Fact]
    public async Task Bypass_replaces_the_entry_on_successful_commit()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key(), ResultCacheFixtures.Result(1))).ShouldBeTrue();

        using (var bypass = await LookupAsync(h, CacheHarness.Key(), bypass: true))
        {
            bypass.Status.ShouldBe(WebSqlResultCacheStatus.Bypass);
            bypass.IsFillLeader.ShouldBeTrue();
            bypass.Hit.ShouldBeNull();
            (await h.Cache.TryCommitAsync(bypass, ResultCacheFixtures.Result(3), Ttl, "default", null, default)).ShouldBeTrue();
        }

        using var hit = await LookupAsync(h, CacheHarness.Key());
        hit.Hit.ShouldNotBeNull().Result.Rows.Length.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bypass_failure_keeps_previous_entry_SECRC12()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key(), ResultCacheFixtures.Result(1))).ShouldBeTrue();

        using (var abandoned = await LookupAsync(h, CacheHarness.Key(), bypass: true))
        {
            abandoned.IsFillLeader.ShouldBeTrue();
        }

        using (var oversize = await LookupAsync(h, CacheHarness.Key(), bypass: true))
        {
            var tooBig = ResultCacheFixtures.Result(5, sizeBytes: 2 * 1024 * 1024);
            (await h.Cache.TryCommitAsync(oversize, tooBig, Ttl, "default", null, default)).ShouldBeFalse();
        }

        using var hit = await LookupAsync(h, CacheHarness.Key());
        hit.Hit.ShouldNotBeNull().Result.Rows.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Ttl_is_capped_at_the_hard_maximum()
    {
        using var h = new CacheHarness();
        (await StoreAsync(h, CacheHarness.Key(), ttl: TimeSpan.FromHours(10))).ShouldBeTrue();

        h.Time.Advance(TimeSpan.FromSeconds(3601));
        using var lookup = await LookupAsync(h, CacheHarness.Key());

        lookup.Status.ShouldBe(WebSqlResultCacheStatus.Miss);
    }

    [Fact]
    public async Task Too_large_marker_is_remembered_for_the_key()
    {
        using var h = new CacheHarness();
        using var fill = await LookupAsync(h, CacheHarness.Key());

        h.Cache.IsMarkedTooLarge("h1").ShouldBeFalse();
        h.Cache.MarkTooLarge(fill, TimeSpan.FromMinutes(1));

        h.Cache.IsMarkedTooLarge("h1").ShouldBeTrue();
        h.Cache.IsMarkedTooLarge("other").ShouldBeFalse();
    }

    [Fact]
    public async Task Disposing_a_lookup_twice_is_safe_and_unregisters_the_flight()
    {
        using var h = new CacheHarness();
        var first = await LookupAsync(h, CacheHarness.Key());
        first.Dispose();
        first.Dispose();

        using var second = await LookupAsync(h, CacheHarness.Key());

        second.IsFillLeader.ShouldBeTrue();
    }

    [Fact]
    public void Reservation_is_available_while_enabled()
    {
        using var h = new CacheHarness();

        using var reservation = h.Cache.TryReserve(CacheHarness.Key()).ShouldNotBeNull();

        reservation.TryGrow(1024).ShouldBeTrue();
    }
}
