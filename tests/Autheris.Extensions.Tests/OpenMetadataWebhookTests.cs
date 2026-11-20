namespace Autheris.Extensions.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Autheris.Extensions.Tests.TestSupport;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookTests
{
    private const string WebhookSecret = "om-webhook-test-secret-488";

    private static (OpenMetadataSyncService Service, List<TableMetadata> Upserts, StubOpenMetadataClient Client) CreateSyncService(
        IDistributedClusterStateProvider? clusterState = null,
        TimeProvider? timeProvider = null,
        bool omitClusterState = false)
    {
        var client = new StubOpenMetadataClient();
        var upserts = new List<TableMetadata>();
        var metadataRepo = new InMemoryTableMetadataRepository { OnUpsert = upserts.Add };
        var consentRepo = new RecordingConsentRepository();
        var epochRepo = new InMemoryPolicyEpochRepository();

        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                DefaultTenantId = "default-tenant",
                WebhookSecret = WebhookSecret
            }
        };

        var effectiveState = omitClusterState
            ? null
            : clusterState ?? new InMemoryClusterStateProvider(timeProvider);

        var service = new OpenMetadataSyncService(
            client,
            metadataRepo,
            consentRepo,
            epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance,
            clusterState: effectiveState,
            timeProvider: timeProvider);

        return (service, upserts, client);
    }

    private static OpenMetadataTable SampleTable(string fqn = "service.db.schema.test_tbl") => new()
    {
        Id = Guid.NewGuid(),
        Name = "test_tbl",
        FullyQualifiedName = fqn,
        Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
    };

    private static (string Payload, string Signature) BuildPayload(Guid eventId, DateTimeOffset timestamp, string fqn = "service.db.schema.test_tbl")
    {
        var ts = timestamp.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{eventId}\",\"timestamp\":{ts},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(payload));
        var sig = "sha256=" + Convert.ToHexStringLower(hash);
        return (payload, sig);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Replayed_event_is_rejected()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var (service, _, client) = CreateSyncService(timeProvider: time);
        client.Tables = [SampleTable()];

        var eventId = Guid.NewGuid();
        var (payload, signature) = BuildPayload(eventId, time.GetUtcNow());

        // First delivery: valid and accepted
        var firstResult = await service.HandleWebhookEventAsync(payload, signature);
        firstResult.ShouldBe(OpenMetadataWebhookOutcome.Processed);

        // Replay with identical eventId: rejected by shared deduplication cache
        var replayResult = await service.HandleWebhookEventAsync(payload, signature);
        replayResult.ShouldBe(OpenMetadataWebhookOutcome.Rejected);

        // Replay with skewed timestamp outside tolerance window (> 5 min): rejected
        var staleTimestamp = time.GetUtcNow().AddMinutes(-10);
        var (stalePayload, staleSig) = BuildPayload(Guid.NewGuid(), staleTimestamp);
        var staleResult = await service.HandleWebhookEventAsync(stalePayload, staleSig);
        staleResult.ShouldBe(OpenMetadataWebhookOutcome.Rejected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Replay_across_two_service_instances_sharing_one_store_is_rejected()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var sharedStore = new InMemoryClusterStateProvider(time);
        var (serviceA, upsertsA, clientA) = CreateSyncService(clusterState: sharedStore, timeProvider: time);
        var (serviceB, upsertsB, clientB) = CreateSyncService(clusterState: sharedStore, timeProvider: time);

        clientA.Tables = [SampleTable()];
        clientB.Tables = [SampleTable()];

        var (payload, signature) = BuildPayload(Guid.NewGuid(), time.GetUtcNow());

        var firstResult = await serviceA.HandleWebhookEventAsync(payload, signature);
        firstResult.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        upsertsA.Count.ShouldBe(1);

        // Second independent instance sharing the same store rejects the replay without per-process state
        var replayResult = await serviceB.HandleWebhookEventAsync(payload, signature);
        replayResult.ShouldBe(OpenMetadataWebhookOutcome.Rejected);
        upsertsB.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Oversized_payload_is_rejected()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var (service, _, _) = CreateSyncService(timeProvider: time);

        var largePadding = new string('a', 2 * 1024 * 1024 + 1024);
        var largePayload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{time.GetUtcNow().ToUnixTimeMilliseconds()},\"data\":\"{largePadding}\"}}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(largePayload));
        var largeSig = "sha256=" + Convert.ToHexStringLower(hash);

        var largeResult = await service.HandleWebhookEventAsync(largePayload, largeSig);
        largeResult.ShouldBe(OpenMetadataWebhookOutcome.Rejected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Store_unavailable_rejects_without_local_fallback(bool returnNull)
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var state = new UnavailableCounterState(returnNull, time);
        var (service, upserts, client) = CreateSyncService(clusterState: state, timeProvider: time);
        client.Tables = [SampleTable()];

        var (payload, signature) = BuildPayload(Guid.NewGuid(), time.GetUtcNow());

        // Store outage: fails closed as Unavailable and records nothing locally
        var duringOutage = await service.HandleWebhookEventAsync(payload, signature);
        duringOutage.ShouldBe(OpenMetadataWebhookOutcome.Unavailable);
        upserts.ShouldBeEmpty();

        // Once the shared store recovers, the retried delivery succeeds (proving no local fallback recorded it)
        state.IsAvailable = true;
        var afterRecovery = await service.HandleWebhookEventAsync(payload, signature);
        afterRecovery.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        upserts.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Missing_store_rejects_as_unavailable()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        var (service, upserts, client) = CreateSyncService(omitClusterState: true, timeProvider: time);
        client.Tables = [SampleTable()];

        var (payload, signature) = BuildPayload(Guid.NewGuid(), time.GetUtcNow());

        var result = await service.HandleWebhookEventAsync(payload, signature);

        result.ShouldBe(OpenMetadataWebhookOutcome.Unavailable);
        upserts.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Skew_check_uses_injected_time_provider()
    {
        var historicalClock = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var time = new MutableTimeProvider(historicalClock);
        var (service, _, client) = CreateSyncService(timeProvider: time);
        client.Tables = [SampleTable()];

        var (matchingPayload, matchingSig) = BuildPayload(Guid.NewGuid(), historicalClock);
        (await service.HandleWebhookEventAsync(matchingPayload, matchingSig))
            .ShouldBe(OpenMetadataWebhookOutcome.Processed);

        var sixMinutesAhead = historicalClock.AddMinutes(6);
        var (skewedPayload, skewedSig) = BuildPayload(Guid.NewGuid(), sixMinutesAhead);
        (await service.HandleWebhookEventAsync(skewedPayload, skewedSig))
            .ShouldBe(OpenMetadataWebhookOutcome.Rejected);

        time.UtcNow = historicalClock.AddMinutes(4);
        var (nowWithinWindowPayload, nowWithinWindowSig) = BuildPayload(Guid.NewGuid(), sixMinutesAhead);
        (await service.HandleWebhookEventAsync(nowWithinWindowPayload, nowWithinWindowSig))
            .ShouldBe(OpenMetadataWebhookOutcome.Processed);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class UnavailableCounterState(bool returnNull, TimeProvider time) : IDistributedClusterStateProvider
    {
        private readonly InMemoryClusterStateProvider _recovered = new(time);
        public bool IsAvailable { get; set; }

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default) =>
            IsAvailable
                ? _recovered.IncrementAsync(key, delta, ttl, ct)
                : returnNull
                    ? ValueTask.FromResult<long?>(null)
                    : throw new ClusterStateUnavailableException("down");

        public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
