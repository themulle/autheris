namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Common;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class WebSqlResultCacheStoreTests
{
    private static readonly TenantId T1 = new("t1");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private WebSqlResultCacheStore Store(ResultCacheLimits limits, int tombstones = 100)
    {
        var store = new WebSqlResultCacheStore(_time, tombstones);
        store.Configure(limits);
        return store;
    }

    private ResultCacheEntry Entry(string hash, string sid, long size, TimeSpan? ttl = null, string tenant = "t1") =>
        ResultCacheFixtures.Entry(hash, sid, size, _time.GetUtcNow(), ttl, tenant);

    [Fact]
    public void Put_and_get_round_trip()
    {
        using var store = Store(ResultCacheFixtures.Limits());

        store.TryPut(Entry("a", "S-1", 100)).ShouldBeTrue();

        store.TryGet("a", T1, "S-1").ShouldNotBeNull().Hash.ShouldBe("a");
        store.TotalBytes.ShouldBe(100);
        store.EntryCount.ShouldBe(1);
    }

    [Fact]
    public void Global_byte_cap_evicts_least_recently_used()
    {
        using var store = Store(ResultCacheFixtures.Limits(total: 300));
        store.TryPut(Entry("a", "S-1", 100)).ShouldBeTrue();
        store.TryPut(Entry("b", "S-2", 100)).ShouldBeTrue();
        store.TryPut(Entry("c", "S-3", 100)).ShouldBeTrue();
        store.TryGet("a", T1, "S-1").ShouldNotBeNull();

        store.TryPut(Entry("d", "S-4", 100)).ShouldBeTrue();

        store.TryGet("b", T1, "S-2").ShouldBeNull();
        store.TryGet("a", T1, "S-1").ShouldNotBeNull();
        store.TotalBytes.ShouldBe(300);
    }

    [Fact]
    public void Entry_over_the_entry_cap_is_rejected()
    {
        using var store = Store(ResultCacheFixtures.Limits(entry: 100));

        store.TryPut(Entry("a", "S-1", 101)).ShouldBeFalse();

        store.EntryCount.ShouldBe(0);
    }

    [Fact]
    public void Entry_expires_lazily()
    {
        using var store = Store(ResultCacheFixtures.Limits());
        store.TryPut(Entry("a", "S-1", 10, TimeSpan.FromSeconds(60))).ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(59));
        store.TryGet("a", T1, "S-1").ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(2));

        store.TryGet("a", T1, "S-1").ShouldBeNull();
        store.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Hit_does_not_extend_ttl_SECRC07()
    {
        using var store = Store(ResultCacheFixtures.Limits());
        store.TryPut(Entry("a", "S-1", 10, TimeSpan.FromSeconds(60))).ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(40));
        store.TryGet("a", T1, "S-1").ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(21));

        store.TryGet("a", T1, "S-1").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Tenant_or_sid_mismatch_never_served_SECRC04()
    {
        using var store = Store(ResultCacheFixtures.Limits());
        store.TryPut(Entry("a", "S-1", 10)).ShouldBeTrue();

        store.TryGet("a", T1, "S-2").ShouldBeNull();
        store.EntryCount.ShouldBe(0);

        store.TryPut(Entry("a", "S-1", 10)).ShouldBeTrue();
        store.TryGet("a", new TenantId("t2"), "S-1").ShouldBeNull();
        store.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void User_cap_evicts_only_own_entries_SECRC14()
    {
        using var store = Store(ResultCacheFixtures.Limits(perUser: 2));
        store.TryPut(Entry("b1", "S-2", 10)).ShouldBeTrue();
        store.TryPut(Entry("a1", "S-1", 10)).ShouldBeTrue();
        store.TryPut(Entry("a2", "S-1", 10)).ShouldBeTrue();

        store.TryPut(Entry("a3", "S-1", 10)).ShouldBeTrue();

        store.TryGet("a1", T1, "S-1").ShouldBeNull();
        store.TryGet("a2", T1, "S-1").ShouldNotBeNull();
        store.TryGet("a3", T1, "S-1").ShouldNotBeNull();
        store.TryGet("b1", T1, "S-2").ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void User_byte_cap_prevents_evicting_other_users_SECRC14()
    {
        using var store = Store(ResultCacheFixtures.Limits(total: 1000, user: 200));
        store.TryPut(Entry("b1", "S-2", 150)).ShouldBeTrue();
        store.TryPut(Entry("b2", "S-2", 40)).ShouldBeTrue();
        store.TryPut(Entry("a1", "S-1", 150)).ShouldBeTrue();

        store.TryPut(Entry("a2", "S-1", 150)).ShouldBeTrue();
        store.TryPut(Entry("a3", "S-1", 201)).ShouldBeFalse();

        store.TryGet("a1", T1, "S-1").ShouldBeNull();
        store.TryGet("a2", T1, "S-1").ShouldNotBeNull();
        store.TryGet("b1", T1, "S-2").ShouldNotBeNull();
        store.TryGet("b2", T1, "S-2").ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Failed_reservation_skips_recording_SECRC14()
    {
        using var store = Store(ResultCacheFixtures.Limits(total: 1000, user: 300));
        using var reservation = store.BeginReservation(T1, "S-1").ShouldNotBeNull();

        reservation.TryGrow(200).ShouldBeTrue();
        reservation.TryGrow(200).ShouldBeFalse();
        store.ReservedBytes.ShouldBe(200);

        using var other = store.BeginReservation(T1, "S-2").ShouldNotBeNull();
        other.TryGrow(700).ShouldBeFalse();
        other.TryGrow(300).ShouldBeTrue();
        other.TryGrow(300).ShouldBeFalse();
    }

    [Fact]
    public void Reservations_count_against_the_global_budget_until_disposed()
    {
        using var store = Store(ResultCacheFixtures.Limits(total: 1000, user: 1000));
        var reservation = store.BeginReservation(T1, "S-1").ShouldNotBeNull();
        reservation.TryGrow(900).ShouldBeTrue();

        store.TryPut(Entry("a", "S-2", 200)).ShouldBeFalse();
        reservation.Dispose();
        reservation.Dispose();

        store.ReservedBytes.ShouldBe(0);
        store.TryPut(Entry("a", "S-2", 200)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Tombstones_are_bounded_SECRC14()
    {
        using var store = Store(ResultCacheFixtures.Limits(), tombstones: 3);

        for (var i = 0; i < 5; i++)
        {
            store.MarkTooLarge($"h{i}", TimeSpan.FromMinutes(1));
        }

        store.TombstoneCount.ShouldBe(3);
        store.IsMarkedTooLarge("h0").ShouldBeFalse();
        store.IsMarkedTooLarge("h4").ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(2));
        store.IsMarkedTooLarge("h4").ShouldBeFalse();
    }

    [Fact]
    public void Replacement_keeps_the_old_entry_when_the_new_one_is_rejected()
    {
        using var store = Store(ResultCacheFixtures.Limits(entry: 100));
        store.TryPut(Entry("a", "S-1", 50)).ShouldBeTrue();

        store.TryPut(Entry("a", "S-1", 500)).ShouldBeFalse();

        store.TryGet("a", T1, "S-1").ShouldNotBeNull().SizeBytes.ShouldBe(50);
    }

    [Fact]
    public void Turning_the_gate_off_clears_everything_and_gauges_follow_the_store()
    {
        using var store = Store(ResultCacheFixtures.Limits());
        store.TryPut(Entry("a", "S-1", 100)).ShouldBeTrue();
        WebSqlResultCacheMetrics.Observed().Bytes.ShouldBeGreaterThanOrEqualTo(100);

        store.Configure(ResultCacheLimits.Off);

        store.EntryCount.ShouldBe(0);
        store.TotalBytes.ShouldBe(0);
        store.TryPut(Entry("b", "S-1", 10)).ShouldBeFalse();
        store.BeginReservation(T1, "S-1").ShouldBeNull();
    }
}
