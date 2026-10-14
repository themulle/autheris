namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// WP-ARC-A-02: dialect-neutral contract of <see cref="IWebhookOutbox"/>. SQLite runs in the unit project; the
/// PostgreSQL and SQL Server subclasses live in the integration project (this file is linked there).
/// A subclass returns <c>null</c> from <see cref="OpenAsync"/> when its database is unavailable (no Docker).
/// </summary>
public abstract class WebhookOutboxContractTests : IAsyncLifetime
{
    protected static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    protected static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly List<object> _disposables = [];

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            if (d is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (d is IDisposable sd)
            {
                sd.Dispose();
            }
        }
    }

    /// <summary>Opens an outbox on a fresh, migrated database; <c>null</c> when the backend is unavailable.</summary>
    protected abstract Task<IWebhookOutbox?> OpenAsync();

    protected T Track<T>(T disposable) where T : notnull
    {
        _disposables.Add(disposable);
        return disposable;
    }

    protected static WebhookOutboxMessage Msg(
        string subscriptionId = "sub-1",
        string? eventId = null,
        WebhookSubscriptionSource source = WebhookSubscriptionSource.Configured,
        string? ownerTenant = "tenant-A",
        DateTimeOffset? nextAttempt = null,
        int maxAttempts = 5) => new(
            Id: Guid.NewGuid().ToString("N"),
            Source: source,
            SubscriptionId: subscriptionId,
            OwnerTenantId: ownerTenant,
            EventTenantId: ownerTenant,
            EventId: eventId ?? Guid.NewGuid().ToString("N"),
            EventType: "autheris.test.event",
            EventSource: "https://autheris.gateway/events",
            EventTime: Now.AddSeconds(-5),
            PayloadJson: "{\"hello\":\"world\"}",
            Attempt: 0,
            MaxAttempts: maxAttempts,
            State: WebhookOutboxState.Pending,
            NextAttemptAt: nextAttempt ?? Now,
            LeaseUntil: null,
            LeaseOwner: null,
            LastStatusCode: null,
            LastErrorCode: null,
            CreatedAt: Now);

    [Fact]
    public async Task Enqueue_SameSubscriptionAndEventTwice_StoresOnce()
    {
        if (await OpenAsync() is not { } outbox) return;

        (await outbox.EnqueueAsync(Msg(eventId: "e1"))).ShouldBeTrue();
        (await outbox.EnqueueAsync(Msg(eventId: "e1"))).ShouldBeFalse();

        (await outbox.CountPendingAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Enqueue_SameEventForDifferentSourceOrSubscription_StoresEach()
    {
        if (await OpenAsync() is not { } outbox) return;

        (await outbox.EnqueueAsync(Msg("s", "e1", WebhookSubscriptionSource.Configured))).ShouldBeTrue();
        (await outbox.EnqueueAsync(Msg("s", "e1", WebhookSubscriptionSource.CloudEvent))).ShouldBeTrue();
        (await outbox.EnqueueAsync(Msg("s2", "e1"))).ShouldBeTrue();

        (await outbox.CountPendingAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Claim_ReturnsDueRowsOnly_WithLeaseAndRoundTrippedFields()
    {
        if (await OpenAsync() is not { } outbox) return;
        var due = Msg(eventId: "due");
        await outbox.EnqueueAsync(due);
        await outbox.EnqueueAsync(Msg(eventId: "later", nextAttempt: Now.AddMinutes(10)));

        var claimed = await outbox.ClaimBatchAsync(10, Now, Lease, "w1");

        var row = claimed.ShouldHaveSingleItem();
        row.EventId.ShouldBe("due");
        row.State.ShouldBe(WebhookOutboxState.InFlight);
        row.LeaseOwner.ShouldBe("w1");
        row.LeaseUntil.ShouldBe(Now + Lease);
        row.PayloadJson.ShouldBe(due.PayloadJson);
        row.EventTime.ShouldBe(due.EventTime);
        row.OwnerTenantId.ShouldBe("tenant-A");
        row.EventTenantId.ShouldBe("tenant-A");
        row.Source.ShouldBe(WebhookSubscriptionSource.Configured);
        row.MaxAttempts.ShouldBe(5);
    }

    [Fact]
    public async Task Claim_IncrementsAttempt()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "e"));

        var first = (await outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        first.Attempt.ShouldBe(1);

        // Crash before completing: the lease expires and the next claim counts as attempt 2 (poison protection).
        var second = (await outbox.ClaimBatchAsync(1, Now + Lease + TimeSpan.FromSeconds(1), Lease, "w2")).Single();
        second.Attempt.ShouldBe(2);
    }

    [Fact]
    public async Task Claim_RespectsLimit_AndOrdersByNextAttempt()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "b", nextAttempt: Now.AddSeconds(-1)));
        await outbox.EnqueueAsync(Msg(eventId: "a", nextAttempt: Now.AddSeconds(-30)));
        await outbox.EnqueueAsync(Msg(eventId: "c", nextAttempt: Now.AddSeconds(-2)));

        var claimed = await outbox.ClaimBatchAsync(2, Now, Lease, "w1");

        claimed.Select(c => c.EventId).ShouldBe(["a", "c"]);
    }

    [Fact]
    public async Task ParallelWorkers_ClaimDisjointBatches()
    {
        if (await OpenAsync() is not { } outbox) return;
        for (var i = 0; i < 40; i++)
        {
            await outbox.EnqueueAsync(Msg(eventId: $"e{i}"));
        }

        var claims = Enumerable.Range(0, 8).Select(i => Task.Run(() => outbox.ClaimBatchAsync(4, Now, Lease, $"w{i}"))).ToArray();
        var results = await Task.WhenAll(claims);

        // Claims are disjoint and respect the limit. A dialect using READPAST may hand a contended worker fewer rows than its
        // limit, so only an upper bound is asserted per round; draining afterwards proves no row was lost or duplicated.
        var ids = results.SelectMany(r => r.Select(m => m.Id)).ToList();
        ids.Count.ShouldBe(ids.Distinct().Count());
        ids.Count.ShouldBeInRange(4, 32);
        for (var i = 0; i < results.Length; i++)
        {
            results[i].Count.ShouldBeLessThanOrEqualTo(4);
            results[i].ShouldAllBe(m => m.LeaseOwner == $"w{i}");
        }

        var all = new HashSet<string>(ids);
        for (var round = 0; round < 20 && all.Count < 40; round++)
        {
            foreach (var m in await outbox.ClaimBatchAsync(40, Now, Lease, "drain"))
            {
                all.Add(m.Id).ShouldBeTrue();
            }
        }

        all.Count.ShouldBe(40);
    }

    [Fact]
    public async Task Purge_MeasuresRetentionFromCompletion_NotFromClaim()
    {
        if (await OpenAsync() is not { } outbox) return;
        var old = Now.AddDays(-30);
        await outbox.EnqueueAsync(Msg("s", "slow", nextAttempt: old));
        var row = (await outbox.ClaimBatchAsync(1, old, Lease, "w1")).Single();
        await outbox.CompleteAsync(row.Id, "w1", 200, Now); // completed recently although claimed long ago

        (await outbox.PurgeAsync(Now.AddHours(-24), Now.AddDays(-14))).ShouldBe(0);
        (await outbox.PurgeAsync(Now.AddMinutes(1), Now.AddDays(-14))).ShouldBe(1);
    }

    [Fact]
    public async Task ActiveLease_IsNotReclaimable_ExpiredLease_IsReclaimable()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "e"));
        await outbox.ClaimBatchAsync(1, Now, Lease, "w1");

        (await outbox.ClaimBatchAsync(1, Now + Lease - TimeSpan.FromSeconds(1), Lease, "w2")).ShouldBeEmpty();
        var reclaimed = await outbox.ClaimBatchAsync(1, Now + Lease, Lease, "w2");

        reclaimed.ShouldHaveSingleItem().LeaseOwner.ShouldBe("w2");
    }

    [Fact]
    public async Task StaleLeaseOwner_CannotComplete()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "e"));
        var first = (await outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        var later = Now + Lease + TimeSpan.FromSeconds(1);
        await outbox.ClaimBatchAsync(1, later, Lease, "w2");

        (await outbox.CompleteAsync(first.Id, "w1", 200, Now)).ShouldBeFalse();
        (await outbox.RescheduleAsync(first.Id, "w1", later, 500, "http_status", Now)).ShouldBeFalse();
        (await outbox.DeadLetterAsync(first.Id, "w1", 500, "http_status", Now)).ShouldBeFalse();
        (await outbox.CompleteAsync(first.Id, "w2", 200, Now)).ShouldBeTrue();
        (await outbox.CompleteAsync(first.Id, "w2", 200, Now)).ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_RemovesRowFromPending_AndDedupeStillHolds()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "e"));
        var row = (await outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();

        (await outbox.CompleteAsync(row.Id, "w1", 204, Now)).ShouldBeTrue();

        (await outbox.CountPendingAsync()).ShouldBe(0);
        (await outbox.ClaimBatchAsync(1, Now + TimeSpan.FromDays(1), Lease, "w1")).ShouldBeEmpty();
        (await outbox.EnqueueAsync(Msg(eventId: "e"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Reschedule_MakesRowDueAgainAtNextAttempt_AndKeepsError()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg(eventId: "e"));
        var row = (await outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        var next = Now.AddMinutes(5);

        (await outbox.RescheduleAsync(row.Id, "w1", next, 503, "http_status", Now)).ShouldBeTrue();

        (await outbox.ClaimBatchAsync(1, next.AddSeconds(-1), Lease, "w2")).ShouldBeEmpty();
        var again = (await outbox.ClaimBatchAsync(1, next, Lease, "w2")).Single();
        again.Attempt.ShouldBe(2);
        again.LastStatusCode.ShouldBe(503);
        again.LastErrorCode.ShouldBe("http_status");
    }

    [Fact]
    public async Task Defer_PushesPendingRows_OfThatSubscriptionOnly()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg("slow", "e1"));
        await outbox.EnqueueAsync(Msg("slow", "e2"));
        await outbox.EnqueueAsync(Msg("slow", "e3", WebhookSubscriptionSource.CloudEvent));
        await outbox.EnqueueAsync(Msg("other", "e4"));
        var until = Now.AddMinutes(5);

        var deferred = await outbox.DeferSubscriptionAsync(WebhookSubscriptionSource.Configured, "slow", until);

        deferred.ShouldBe(2);
        var now = (await outbox.ClaimBatchAsync(10, Now, TimeSpan.FromHours(1), "w1")).Select(m => m.EventId).Order().ToList();
        now.ShouldBe(["e3", "e4"]);
        var later = await outbox.ClaimBatchAsync(10, until, Lease, "w1");
        later.Select(m => m.EventId).Order().ShouldBe(["e1", "e2"]);
        later.ShouldAllBe(m => m.Attempt == 1); // deferral does not consume attempts
    }

    [Fact]
    public async Task Purge_RemovesOldDeliveredAndDead_KeepsPendingAndRecent()
    {
        if (await OpenAsync() is not { } outbox) return;
        var old = Now.AddDays(-30);
        await outbox.EnqueueAsync(Msg("s", "delivered-old", nextAttempt: old));
        await outbox.EnqueueAsync(Msg("s", "dead-old", nextAttempt: old));
        var claimed = await outbox.ClaimBatchAsync(10, old, Lease, "w1");
        await outbox.CompleteAsync(claimed.Single(m => m.EventId == "delivered-old").Id, "w1", 200, old);
        await outbox.DeadLetterAsync(claimed.Single(m => m.EventId == "dead-old").Id, "w1", 500, "max_attempts_exceeded", old);
        await outbox.EnqueueAsync(Msg("s", "delivered-new", nextAttempt: Now));
        await outbox.EnqueueAsync(Msg("s", "pending", nextAttempt: Now.AddHours(1)));
        var recent = (await outbox.ClaimBatchAsync(10, Now, Lease, "w1")).Single();
        await outbox.CompleteAsync(recent.Id, "w1", 200, Now);

        var purged = await outbox.PurgeAsync(Now.AddHours(-24), Now.AddDays(-14));

        purged.ShouldBe(2);
        (await outbox.ListDeadLettersAsync(new WebhookOwnerScope("tenant-A", false), 10)).ShouldBeEmpty();
        (await outbox.CountPendingAsync()).ShouldBe(1);
        (await outbox.EnqueueAsync(Msg("s", "delivered-old"))).ShouldBeTrue();
        (await outbox.EnqueueAsync(Msg("s", "delivered-new"))).ShouldBeFalse();
    }

    [Fact]
    public async Task ListDeadLetters_IsTenantScoped()
    {
        if (await OpenAsync() is not { } outbox) return;
        await outbox.EnqueueAsync(Msg("sa", "ea", ownerTenant: "tenant-A"));
        await outbox.EnqueueAsync(Msg("sb", "eb", ownerTenant: "tenant-B"));
        await outbox.EnqueueAsync(Msg("sc", "ec", ownerTenant: null));
        foreach (var m in await outbox.ClaimBatchAsync(10, Now, Lease, "w1"))
        {
            await outbox.DeadLetterAsync(m.Id, "w1", 404, "http_status", Now);
        }

        var a = await outbox.ListDeadLettersAsync(new WebhookOwnerScope("tenant-A", false), 10);
        var b = await outbox.ListDeadLettersAsync(new WebhookOwnerScope("tenant-B", false), 10);
        var cluster = await outbox.ListDeadLettersAsync(new WebhookOwnerScope(null, true), 10);

        a.ShouldHaveSingleItem().EventId.ShouldBe("ea");
        a[0].LastErrorCode.ShouldBe("http_status");
        a[0].LastStatusCode.ShouldBe(404);
        b.ShouldHaveSingleItem().EventId.ShouldBe("eb");
        cluster.ShouldHaveSingleItem().EventId.ShouldBe("ec");
        typeof(WebhookDeadLetter).GetProperties().ShouldNotContain(p => p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));

        await Should.ThrowAsync<ArgumentException>(() => outbox.ListDeadLettersAsync(new WebhookOwnerScope(null, false), 10));
        await Should.ThrowAsync<ArgumentException>(() => outbox.ListDeadLettersAsync(new WebhookOwnerScope("  ", false), 10));
    }
}
