using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IWebhookOutbox</c> (durable webhook outbox with leases) across PostgreSQL and SQL Server engines.
/// Time is always passed in. Completion calls are fenced by the lease owner, dead letters are listed per owner scope only,
/// and every claim and list limit is clamped to 1..1000.
/// </summary>
public abstract class WebhookOutboxContract : GovernanceContractBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private IWebhookOutbox Outbox => Store.Services.GetRequiredService<IWebhookOutbox>();

    private WebhookOutboxMessage Msg(
        string subscriptionId = "sub-1",
        string? eventId = null,
        WebhookSubscriptionSource source = WebhookSubscriptionSource.Configured,
        string? ownerTenant = "default",
        DateTimeOffset? nextAttempt = null,
        int maxAttempts = 5)
    {
        var owner = ownerTenant == "default" ? Store.TenantA.Value : ownerTenant;
        return new(
            Id: Guid.NewGuid().ToString("N"),
            Source: source,
            SubscriptionId: subscriptionId,
            OwnerTenantId: owner,
            EventTenantId: owner,
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
    }

    [RequiresContainerRuntimeFact]
    public async Task Enqueue_SameEventForDifferentSourceOrSubscription_StoresEach()
    {
        if (!Store.IsAvailable) return;

        (await Outbox.EnqueueAsync(Msg("s", "e1", WebhookSubscriptionSource.Configured))).ShouldBeTrue();
        (await Outbox.EnqueueAsync(Msg("s", "e1", WebhookSubscriptionSource.CloudEvent))).ShouldBeTrue();
        (await Outbox.EnqueueAsync(Msg("s2", "e1"))).ShouldBeTrue();

        (await Outbox.CountPendingAsync()).ShouldBe(3);
    }

    [RequiresContainerRuntimeFact]
    public async Task Claim_ReturnsDueRowsOnly_WithLeaseAndRoundTrippedFields()
    {
        if (!Store.IsAvailable) return;
        var due = Msg(eventId: "due");
        await Outbox.EnqueueAsync(due);
        await Outbox.EnqueueAsync(Msg(eventId: "later", nextAttempt: Now.AddMinutes(10)));

        var claimed = await Outbox.ClaimBatchAsync(10, Now, Lease, "w1");

        var row = claimed.ShouldHaveSingleItem();
        row.EventId.ShouldBe("due");
        row.State.ShouldBe(WebhookOutboxState.InFlight);
        row.LeaseOwner.ShouldBe("w1");
        row.LeaseUntil.ShouldBe(Now + Lease);
        row.PayloadJson.ShouldBe(due.PayloadJson);
        row.EventTime.ShouldBe(due.EventTime);
        row.OwnerTenantId.ShouldBe(Store.TenantA.Value);
        row.EventTenantId.ShouldBe(Store.TenantA.Value);
        row.Source.ShouldBe(WebhookSubscriptionSource.Configured);
        row.MaxAttempts.ShouldBe(5);
    }

    [RequiresContainerRuntimeFact]
    public async Task Claim_IncrementsAttempt()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg(eventId: "e"));

        var first = (await Outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        first.Attempt.ShouldBe(1);

        // Crash before completing: the lease expires and the next claim counts as attempt 2 (poison protection).
        var second = (await Outbox.ClaimBatchAsync(1, Now + Lease + TimeSpan.FromSeconds(1), Lease, "w2")).Single();
        second.Attempt.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public async Task ParallelWorkers_ClaimDisjointBatches()
    {
        if (!Store.IsAvailable) return;
        for (var i = 0; i < 40; i++)
        {
            await Outbox.EnqueueAsync(Msg(eventId: $"e{i}"));
        }

        var claims = Enumerable.Range(0, 8).Select(i => Task.Run(() => Outbox.ClaimBatchAsync(4, Now, Lease, $"w{i}"))).ToArray();
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
            foreach (var m in await Outbox.ClaimBatchAsync(40, Now, Lease, "drain"))
            {
                all.Add(m.Id).ShouldBeTrue();
            }
        }

        all.Count.ShouldBe(40);
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_MeasuresRetentionFromCompletion_NotFromClaim()
    {
        if (!Store.IsAvailable) return;
        var old = Now.AddDays(-30);
        await Outbox.EnqueueAsync(Msg("s", "slow", nextAttempt: old));
        var row = (await Outbox.ClaimBatchAsync(1, old, Lease, "w1")).Single();
        await Outbox.CompleteAsync(row.Id, "w1", 200, Now); // completed recently although claimed long ago

        (await Outbox.PurgeAsync(Now.AddHours(-24), Now.AddDays(-14))).ShouldBe(0);
        (await Outbox.PurgeAsync(Now.AddMinutes(1), Now.AddDays(-14))).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task ActiveLease_IsNotReclaimable_ExpiredLease_IsReclaimable()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg(eventId: "e"));
        await Outbox.ClaimBatchAsync(1, Now, Lease, "w1");

        (await Outbox.ClaimBatchAsync(1, Now + Lease - TimeSpan.FromSeconds(1), Lease, "w2")).ShouldBeEmpty();
        var reclaimed = await Outbox.ClaimBatchAsync(1, Now + Lease, Lease, "w2");

        reclaimed.ShouldHaveSingleItem().LeaseOwner.ShouldBe("w2");
    }

    [RequiresContainerRuntimeFact]
    public async Task Reschedule_MakesRowDueAgainAtNextAttempt_AndKeepsError()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg(eventId: "e"));
        var row = (await Outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        var next = Now.AddMinutes(5);

        (await Outbox.RescheduleAsync(row.Id, "w1", next, 503, "http_status", Now)).ShouldBeTrue();

        (await Outbox.ClaimBatchAsync(1, next.AddSeconds(-1), Lease, "w2")).ShouldBeEmpty();
        var again = (await Outbox.ClaimBatchAsync(1, next, Lease, "w2")).Single();
        again.Attempt.ShouldBe(2);
        again.LastStatusCode.ShouldBe(503);
        again.LastErrorCode.ShouldBe("http_status");
    }

    [RequiresContainerRuntimeFact]
    public async Task Defer_PushesPendingRows_OfThatSubscriptionOnly()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg("slow", "e1"));
        await Outbox.EnqueueAsync(Msg("slow", "e2"));
        await Outbox.EnqueueAsync(Msg("slow", "e3", WebhookSubscriptionSource.CloudEvent));
        await Outbox.EnqueueAsync(Msg("other", "e4"));
        var until = Now.AddMinutes(5);

        var deferred = await Outbox.DeferSubscriptionAsync(WebhookSubscriptionSource.Configured, "slow", until);

        deferred.ShouldBe(2);
        var now = (await Outbox.ClaimBatchAsync(10, Now, TimeSpan.FromHours(1), "w1")).Select(m => m.EventId).Order().ToList();
        now.ShouldBe(["e3", "e4"]);
        var later = await Outbox.ClaimBatchAsync(10, until, Lease, "w1");
        later.Select(m => m.EventId).Order().ShouldBe(["e1", "e2"]);
        later.ShouldAllBe(m => m.Attempt == 1); // deferral does not consume attempts
    }

    [RequiresContainerRuntimeFact]
    public async Task Purge_RemovesOldDeliveredAndDead_KeepsPendingAndRecent()
    {
        if (!Store.IsAvailable) return;
        var old = Now.AddDays(-30);
        await Outbox.EnqueueAsync(Msg("s", "delivered-old", nextAttempt: old));
        await Outbox.EnqueueAsync(Msg("s", "dead-old", nextAttempt: old));
        var claimed = await Outbox.ClaimBatchAsync(10, old, Lease, "w1");
        await Outbox.CompleteAsync(claimed.Single(m => m.EventId == "delivered-old").Id, "w1", 200, old);
        await Outbox.DeadLetterAsync(claimed.Single(m => m.EventId == "dead-old").Id, "w1", 500, "max_attempts_exceeded", old);
        await Outbox.EnqueueAsync(Msg("s", "delivered-new", nextAttempt: Now));
        await Outbox.EnqueueAsync(Msg("s", "pending", nextAttempt: Now.AddHours(1)));
        var recent = (await Outbox.ClaimBatchAsync(10, Now, Lease, "w1")).Single();
        await Outbox.CompleteAsync(recent.Id, "w1", 200, Now);

        var purged = await Outbox.PurgeAsync(Now.AddHours(-24), Now.AddDays(-14));

        purged.ShouldBe(2);
        (await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(Store.TenantA.Value, false), 10)).ShouldBeEmpty();
        (await Outbox.CountPendingAsync()).ShouldBe(1);
        (await Outbox.EnqueueAsync(Msg("s", "delivered-old"))).ShouldBeTrue();
        (await Outbox.EnqueueAsync(Msg("s", "delivered-new"))).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task EventSurvivesRepositoryRestart()
    {
        if (!Store.IsAvailable) return;
        (await Outbox.EnqueueAsync(Msg(eventId: "persisted"))).ShouldBeTrue();

        await using var second = await Store.CreateAdditionalStoreAsync();
        var claimed = await second.Services.GetRequiredService<IWebhookOutbox>().ClaimBatchAsync(5, Now, Lease, "w1");

        claimed.ShouldHaveSingleItem().EventId.ShouldBe("persisted");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg("sa", "ea", ownerTenant: Store.TenantA.Value));
        await Outbox.EnqueueAsync(Msg("sb", "eb", ownerTenant: Store.TenantB.Value));
        await Outbox.EnqueueAsync(Msg("sc", "ec", ownerTenant: null));
        foreach (var m in await Outbox.ClaimBatchAsync(10, Now, Lease, "w1"))
        {
            await Outbox.DeadLetterAsync(m.Id, "w1", 404, "http_status", Now);
        }

        var a = await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(Store.TenantA.Value, false), 10);
        var b = await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(Store.TenantB.Value, false), 10);
        var cluster = await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(null, true), 10);

        a.ShouldHaveSingleItem().EventId.ShouldBe("ea");
        a[0].LastErrorCode.ShouldBe("http_status");
        a[0].LastStatusCode.ShouldBe(404);
        b.ShouldHaveSingleItem().EventId.ShouldBe("eb");
        cluster.ShouldHaveSingleItem().EventId.ShouldBe("ec");
        typeof(WebhookDeadLetter).GetProperties().ShouldNotContain(p => p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        // The write guard is the lease owner: a worker that lost its lease can neither complete, reschedule nor dead-letter the row.
        await Outbox.EnqueueAsync(Msg(eventId: "e"));
        var first = (await Outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();
        var later = Now + Lease + TimeSpan.FromSeconds(1);
        await Outbox.ClaimBatchAsync(1, later, Lease, "w2");

        (await Outbox.CompleteAsync(first.Id, "w1", 200, Now)).ShouldBeFalse();
        (await Outbox.RescheduleAsync(first.Id, "w1", later, 500, "http_status", Now)).ShouldBeFalse();
        (await Outbox.DeadLetterAsync(first.Id, "w1", 500, "http_status", Now)).ShouldBeFalse();
        (await Outbox.CompleteAsync(first.Id, "w2", 200, Now)).ShouldBeTrue();
        (await Outbox.CompleteAsync(first.Id, "w2", 200, Now)).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Outbox.ListDeadLettersAsync(new WebhookOwnerScope(null, false), 10));
        await Should.ThrowAsync<ArgumentException>(() => Outbox.ListDeadLettersAsync(new WebhookOwnerScope("  ", false), 10));
        await Should.ThrowAsync<ArgumentException>(() => Outbox.ClaimBatchAsync(1, Now, Lease, " "));
        await Should.ThrowAsync<ArgumentException>(() => Outbox.DeferSubscriptionAsync(WebhookSubscriptionSource.Configured, " ", Now));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        (await Outbox.EnqueueAsync(Msg(eventId: "e1"))).ShouldBeTrue();
        (await Outbox.EnqueueAsync(Msg(eventId: "e1"))).ShouldBeFalse();

        (await Outbox.CountPendingAsync()).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg(eventId: "e"));
        var row = (await Outbox.ClaimBatchAsync(1, Now, Lease, "w1")).Single();

        // A duplicate (source, subscription, event) key is reported as false, never as an exception, also after completion.
        (await Outbox.CompleteAsync(row.Id, "w1", 204, Now)).ShouldBeTrue();

        (await Outbox.CountPendingAsync()).ShouldBe(0);
        (await Outbox.ClaimBatchAsync(1, Now + TimeSpan.FromDays(1), Lease, "w1")).ShouldBeEmpty();
        (await Outbox.EnqueueAsync(Msg(eventId: "e"))).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg(eventId: "b", nextAttempt: Now.AddSeconds(-1)));
        await Outbox.EnqueueAsync(Msg(eventId: "a", nextAttempt: Now.AddSeconds(-30)));
        await Outbox.EnqueueAsync(Msg(eventId: "c", nextAttempt: Now.AddSeconds(-2)));

        var claimed = await Outbox.ClaimBatchAsync(2, Now, Lease, "w1");

        claimed.Select(c => c.EventId).ShouldBe(["a", "c"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        for (var i = 0; i < 3; i++)
        {
            await Outbox.EnqueueAsync(Msg(eventId: $"e{i}"));
        }

        // Limits are clamped to 1..1000: a non-positive limit still claims one row, an absurd limit is bounded and returns every due row.
        (await Outbox.ClaimBatchAsync(0, Now, Lease, "w1")).Count.ShouldBe(1);
        (await Outbox.ClaimBatchAsync(int.MaxValue, Now, Lease, "w2")).Count.ShouldBe(2);

        foreach (var m in await Outbox.ClaimBatchAsync(10, Now + Lease, Lease, "w3"))
        {
            await Outbox.DeadLetterAsync(m.Id, "w3", 500, "http_status", Now);
        }

        var scope = new WebhookOwnerScope(Store.TenantA.Value, false);
        (await Outbox.ListDeadLettersAsync(scope, -5)).Count.ShouldBe(1);
        (await Outbox.ListDeadLettersAsync(scope, int.MaxValue)).Count.ShouldBe(3);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        await Outbox.EnqueueAsync(Msg("s", "done", nextAttempt: Now));
        await Outbox.EnqueueAsync(Msg("s", "dead", nextAttempt: Now));
        var claimed = await Outbox.ClaimBatchAsync(10, Now, Lease, "w1");
        await Outbox.CompleteAsync(claimed.Single(m => m.EventId == "done").Id, "w1", 200, Now);
        await Outbox.DeadLetterAsync(claimed.Single(m => m.EventId == "dead").Id, "w1", 500, "max_attempts_exceeded", Now);

        // Delivered and dead rows are never claimable or pending again; once purged they are not listed either.
        (await Outbox.CountPendingAsync()).ShouldBe(0);
        (await Outbox.ClaimBatchAsync(10, Now + TimeSpan.FromDays(1), Lease, "w2")).ShouldBeEmpty();
        (await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(Store.TenantA.Value, false), 10)).ShouldHaveSingleItem().EventId.ShouldBe("dead");

        (await Outbox.PurgeAsync(Now.AddMinutes(1), Now.AddMinutes(1))).ShouldBe(2);
        (await Outbox.ListDeadLettersAsync(new WebhookOwnerScope(Store.TenantA.Value, false), 10)).ShouldBeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlWebhookOutboxContractTests(GovernancePostgresFixture fixture) : WebhookOutboxContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "webhook_outbox_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerWebhookOutboxContractTests(GovernanceSqlServerFixture fixture) : WebhookOutboxContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "webhook_outbox_contract");
}
