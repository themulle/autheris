namespace Autheris.Modules.OpenMetadata.Tests;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenMetadataWebhookTests
{
    private const string WebhookSecret = "om-webhook-test-secret-488";

    private static (OpenMetadataSyncService Service, ITableMetadataRepository MetadataRepo, IOpenMetadataClient Client) CreateSyncService(
        IDistributedClusterStateProvider? clusterState = null)
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

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

        var service = new OpenMetadataSyncService(
            client,
            metadataRepo,
            consentRepo,
            epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance,
            clusterState: clusterState);

        return (service, metadataRepo, client);
    }

    private static string BuildPayload(Guid eventId, DateTimeOffset timestamp, string fqn = "service.db.schema.test_tbl")
    {
        var ts = timestamp.ToUnixTimeMilliseconds();
        return $"{{\"id\":\"{eventId}\",\"timestamp\":{ts},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Replayed_event_is_rejected()
    {
        var (service, _, client) = CreateSyncService();
        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "test_tbl",
            FullyQualifiedName = "service.db.schema.test_tbl",
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };
        client.GetTableByFqnAsync("service.db.schema.test_tbl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var payload = BuildPayload(eventId, now);

        // First delivery: valid and accepted
        var firstResult = await service.HandleVerifiedAsync(payload);
        firstResult.ShouldBeTrue();

        // Replay with identical eventId: rejected by deduplication cache
        var replayResult = await service.HandleVerifiedAsync(payload);
        replayResult.ShouldBeFalse();

        // Replay with skewed timestamp outside tolerance window (> 5 min): rejected
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var stalePayload = BuildPayload(Guid.NewGuid(), staleTimestamp);
        var staleResult = await service.HandleVerifiedAsync(stalePayload);
        staleResult.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Buffer_is_bounded()
    {
        var (service, _, _) = CreateSyncService();

        // 1. Oversized body (> 2 MB) is rejected before processing
        var largePadding = new string('a', 2 * 1024 * 1024 + 1024);
        var largePayload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"data\":\"{largePadding}\"}}";

        var largeResult = await service.HandleVerifiedAsync(largePayload);
        largeResult.ShouldBeFalse();

        // 2. Deduplication cache / buffer remains bounded
        OpenMetadataSyncService.ProcessedWebhookEventsCount.ShouldBeLessThanOrEqualTo(10_000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Cluster_dedup_unavailable_rejects_webhook(bool returnNull)
    {
        var clusterState = new ToggleableClusterState(returnNull);
        var (service, _, client) = CreateSyncService(clusterState);
        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "test_tbl",
            FullyQualifiedName = "service.db.schema.test_tbl",
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };
        client.GetTableByFqnAsync("service.db.schema.test_tbl", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var eventId = Guid.NewGuid();
        var payload = BuildPayload(eventId, DateTimeOffset.UtcNow);

        // When cluster dedup is configured but unavailable (throws or returns null), fail closed and release in-memory entry.
        var rejected = await service.HandleVerifiedAsync(payload);
        rejected.ShouldBeFalse();

        // Recovery: because the in-memory entry was released on cluster failure, a retry with the same eventId succeeds.
        clusterState.IsAvailable = true;
        var accepted = await service.HandleVerifiedAsync(payload);
        accepted.ShouldBeTrue();
    }

    private sealed class ToggleableClusterState(bool returnNull) : IDistributedClusterStateProvider
    {
        private readonly InMemoryClusterStateProvider _recovered = new();
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
