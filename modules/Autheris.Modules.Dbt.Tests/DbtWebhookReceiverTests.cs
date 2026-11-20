namespace Autheris.Modules.Dbt.Tests;

using System;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtWebhookReceiverTests
{
    [Fact]
    public async Task ProcessWebhookAsync_WithValidPayloadAndSignature_ReturnsSuccess()
    {
        var secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var now = time.GetUtcNow().ToString("O");
        var payload = $$"""
        {
            "eventId": "evt_999",
            "eventType": "job_run.completed",
            "timestamp": "{{now}}",
            "accountId": 42,
            "data": {
                "jobId": 101,
                "jobName": "nightly_marts",
                "runId": 5555,
                "runStatus": "Success",
                "environmentId": 7,
                "dbtVersion": "1.8.0"
            }
        }
        """;

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(
            options,
            circuitBreaker,
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new InMemoryClusterStateProvider(time),
            timeProvider: time);

        var result = await receiver.HandleVerifiedAsync(payload);

        result.Success.ShouldBeTrue();
        result.StoreUnavailable.ShouldBeFalse();
        result.RunId.ShouldBe(5555);
        result.EventType.ShouldBe("job_run.completed");
        result.Message.ShouldContain("Success");
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithSkewedTimestamp_RejectsRequest()
    {
        var secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var pastTime = time.GetUtcNow().AddMinutes(-30).ToString("O");
        var payload = $$"""
        {
            "eventId": "evt_skewed",
            "eventType": "job_run.completed",
            "timestamp": "{{pastTime}}",
            "data": { "runId": 123 }
        }
        """;

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(
            options,
            circuitBreaker,
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new InMemoryClusterStateProvider(time),
            timeProvider: time);

        var result = await receiver.HandleVerifiedAsync(payload);

        result.Success.ShouldBeFalse();
        result.StoreUnavailable.ShouldBeFalse();
        result.Message.ShouldContain("replay window");
    }

    [Fact]
    public async Task ProcessWebhookAsync_DuplicateEventId_SkipsDuplicate()
    {
        var secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var now = time.GetUtcNow().ToString("O");
        var eventId = "evt_dedup_" + Guid.NewGuid().ToString("N");
        var payload = $$"""
        {
            "eventId": "{{eventId}}",
            "eventType": "job_run.completed",
            "timestamp": "{{now}}",
            "data": { "runId": 777 }
        }
        """;

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(
            options,
            circuitBreaker,
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new InMemoryClusterStateProvider(time),
            timeProvider: time);

        var first = await receiver.HandleVerifiedAsync(payload);
        first.Success.ShouldBeTrue();
        first.Message.ShouldNotContain("Duplicate");

        var duplicate = await receiver.HandleVerifiedAsync(payload);
        duplicate.Success.ShouldBeTrue();
        duplicate.Message.ShouldContain("Duplicate event");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Malformed_payload_message_is_fixed()
    {
        const string secret = "test-secret-key";
        const string malformed = "{not-valid-json-canary";
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var receiver = new DbtWebhookReceiver(
            options,
            new NoopCircuitBreaker(),
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new InMemoryClusterStateProvider());

        var result = await receiver.HandleVerifiedAsync(malformed);

        result.Success.ShouldBeFalse();
        result.StoreUnavailable.ShouldBeFalse();
        result.Message.ShouldBe("Malformed payload.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cluster_dedup_entries_expire_after_ttl_and_reject_across_instances()
    {
        const string secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var sharedStore = new InMemoryClusterStateProvider(time);
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var receiverA = new DbtWebhookReceiver(options, new NoopCircuitBreaker(), NullLogger<DbtWebhookReceiver>.Instance, clusterState: sharedStore, timeProvider: time);
        var receiverB = new DbtWebhookReceiver(options, new NoopCircuitBreaker(), NullLogger<DbtWebhookReceiver>.Instance, clusterState: sharedStore, timeProvider: time);

        // 1. Replay across two independent receiver instances sharing one cluster store is detected.
        var payload = $$$"""{"eventId":"evt_shared","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var first = await receiverA.HandleVerifiedAsync(payload);
        first.Success.ShouldBeTrue();
        first.Message.ShouldNotContain("Duplicate");

        var dup = await receiverB.HandleVerifiedAsync(payload);
        dup.Success.ShouldBeTrue();
        dup.Message.ShouldContain("Duplicate");

        // 2. Entries expire after DedupTtl.
        time.Advance(DbtWebhookReceiver.DedupTtl + TimeSpan.FromMinutes(1));
        var freshPayloadAfterTtl = $$$"""{"eventId":"evt_shared","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var afterExpiry = await receiverA.HandleVerifiedAsync(freshPayloadAfterTtl);
        afterExpiry.Success.ShouldBeTrue();
        afterExpiry.Message.ShouldNotContain("Duplicate");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dedup_ttl_not_shorter_than_replay_window()
    {
        DbtWebhookReceiver.DedupTtl.ShouldBeGreaterThanOrEqualTo(DbtWebhookReceiver.ReplaySkewWindow * 2);

        const string secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var receiver = new DbtWebhookReceiver(
            options,
            new NoopCircuitBreaker(),
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new InMemoryClusterStateProvider(time),
            timeProvider: time);

        var t0 = time.GetUtcNow();
        var payload = $$$"""{"eventId":"evt_window","eventType":"job_run.completed","timestamp":"{{{t0:O}}}"}""";
        var first = await receiver.HandleVerifiedAsync(payload);
        first.Success.ShouldBeTrue();
        first.Message.ShouldNotContain("Duplicate");

        // Still inside 2x replay window: replay with valid timestamp at +4m MUST still be blocked by dedup.
        time.Advance(TimeSpan.FromMinutes(4));
        var replay = $$$"""{"eventId":"evt_window","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var second = await receiver.HandleVerifiedAsync(replay);
        second.Success.ShouldBeTrue();
        second.Message.ShouldContain("Duplicate");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Security")]
    public async Task Store_unavailable_rejects_in_every_mode(bool returnNull, bool ignoreTolerance)
    {
        const string secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var state = new FailingClusterState(returnNull, time);
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret },
            Insecure = new InsecureGettingStartedOptions { warn_ignore_webhook_timestamp_tolerance = ignoreTolerance }
        });
        var receiver = new DbtWebhookReceiver(
            options,
            new NoopCircuitBreaker(),
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: state,
            timeProvider: time);

        var payload = $$$"""{"eventId":"evt_cluster_fail","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";

        var res = await receiver.HandleVerifiedAsync(payload);

        res.Success.ShouldBeFalse();
        res.StoreUnavailable.ShouldBeTrue();
        res.Message.ShouldBe("Webhook deduplication unavailable.");

        // Once the shared store recovers, the retried delivery is processed cleanly (no local fallback recorded).
        state.IsAvailable = true;
        var recovered = await receiver.HandleVerifiedAsync(payload);
        recovered.Success.ShouldBeTrue();
        recovered.Message.ShouldNotContain("Duplicate");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Missing_store_rejects_with_store_unavailable(bool ignoreTolerance)
    {
        const string secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret },
            Insecure = new InsecureGettingStartedOptions { warn_ignore_webhook_timestamp_tolerance = ignoreTolerance }
        });
        var receiver = new DbtWebhookReceiver(
            options,
            new NoopCircuitBreaker(),
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: null,
            timeProvider: time);

        var payload = $$$"""{"eventId":"evt_missing_store","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var res = await receiver.HandleVerifiedAsync(payload);

        res.Success.ShouldBeFalse();
        res.StoreUnavailable.ShouldBeTrue();
        res.Message.ShouldBe("Webhook deduplication unavailable.");
    }

    private sealed class NoopCircuitBreaker : IDbtHealthCircuitBreaker
    {
        public ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult(new DbtHealthState(table, DbtModelHealthStatus.Healthy, [], DateTimeOffset.UtcNow));
        public Task<DbtHealthState> SetTableHealthAsync(TableIdentifier table, DbtModelHealthStatus status, IReadOnlyList<DbtTestFailure> failures, CancellationToken ct = default)
            => Task.FromResult(new DbtHealthState(table, status, failures, DateTimeOffset.UtcNow));
        public Task<IReadOnlyDictionary<TableIdentifier, DbtHealthState>> GetAllHealthStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, DbtHealthState>>(new Dictionary<TableIdentifier, DbtHealthState>());
        public Task<DbtRunResultsReport> RecordRunResultsAsync(System.IO.Stream runResultsStream, TenantId tenant, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FailingClusterState(bool returnNull, TimeProvider time) : IDistributedClusterStateProvider
    {
        private readonly InMemoryClusterStateProvider _recovered = new(time);
        public bool IsAvailable { get; set; }

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default) =>
            IsAvailable
                ? _recovered.IncrementAsync(key, delta, ttl, ct)
                : returnNull
                    ? ValueTask.FromResult<long?>(null)
                    : throw new ClusterStateUnavailableException("down");
        public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
            => throw new ClusterStateUnavailableException("down");
        public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
    }
}
