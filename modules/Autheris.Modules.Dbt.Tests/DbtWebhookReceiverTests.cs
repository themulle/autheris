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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtWebhookReceiverTests
{
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

    private sealed class FailingClusterState(bool returnNull = false) : IDistributedClusterStateProvider
    {
        public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) => throw new ClusterStateUnavailableException("down");
        public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
            => returnNull ? ValueTask.FromResult<long?>(null) : throw new ClusterStateUnavailableException("down");
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

    private sealed class CountingWarningLogger : ILogger<DbtWebhookReceiver>
    {
        public int WarningCount { get; private set; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("reached capacity", StringComparison.Ordinal))
            {
                WarningCount++;
            }
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
    [Fact]
    public async Task ProcessWebhookAsync_WithValidPayloadAndSignature_ReturnsSuccess()
    {
        var secret = "test-secret-key";
        var now = DateTimeOffset.UtcNow.ToString("O");
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
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.HandleVerifiedAsync(payload);

        result.Success.ShouldBeTrue();
        result.RunId.ShouldBe(5555);
        result.EventType.ShouldBe("job_run.completed");
        result.Message.ShouldContain("Success");
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithSkewedTimestamp_RejectsRequest()
    {
        var secret = "test-secret-key";
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O");
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
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.HandleVerifiedAsync(payload);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("replay window");
    }

    [Fact]
    public async Task ProcessWebhookAsync_DuplicateEventId_SkipsDuplicate()
    {
        var secret = "test-secret-key";
        var now = DateTimeOffset.UtcNow.ToString("O");
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
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var first = await receiver.HandleVerifiedAsync(payload);
        first.Success.ShouldBeTrue();

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
        var receiver = new DbtWebhookReceiver(options, new NoopCircuitBreaker(), NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.HandleVerifiedAsync(malformed);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Malformed payload.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dedup_entries_expire_and_stay_bounded()
    {
        const string secret = "test-secret-key";
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var logger = new CountingWarningLogger();
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var receiver = new DbtWebhookReceiver(options, new NoopCircuitBreaker(), logger, timeProvider: time);

        // 1. Capacity eviction stays bounded at 10,000 and eviction warning is throttled to once per minute.
        for (var i = 0; i < DbtWebhookReceiver.MaxDedupEntries + 25; i++)
        {
            var payload = $$$"""{"eventId":"evt_{{{i}}}","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
            var res = await receiver.HandleVerifiedAsync(payload);
            res.Success.ShouldBeTrue();
            res.Message.ShouldNotContain("Duplicate");
        }

        receiver.ActiveDedupCount.ShouldBeLessThanOrEqualTo(DbtWebhookReceiver.MaxDedupEntries);
        logger.WarningCount.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(61));
        var payloadAfterMinute = $$$"""{"eventId":"evt_after_min","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var resAfterMin = await receiver.HandleVerifiedAsync(payloadAfterMinute);
        resAfterMin.Success.ShouldBeTrue();
        logger.WarningCount.ShouldBe(2);

        // 2. Entries expire after 24h TTL.
        var expiringPayload = $$$"""{"eventId":"evt_exp","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var first = await receiver.HandleVerifiedAsync(expiringPayload);
        first.Message.ShouldNotContain("Duplicate");

        var dup = await receiver.HandleVerifiedAsync(expiringPayload);
        dup.Message.ShouldContain("Duplicate");

        time.Advance(DbtWebhookReceiver.DedupTtl + TimeSpan.FromMinutes(1));
        var freshPayloadAfterTtl = $$$"""{"eventId":"evt_exp","eventType":"job_run.completed","timestamp":"{{{time.GetUtcNow():O}}}"}""";
        var afterExpiry = await receiver.HandleVerifiedAsync(freshPayloadAfterTtl);
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
        var receiver = new DbtWebhookReceiver(options, new NoopCircuitBreaker(), NullLogger<DbtWebhookReceiver>.Instance, timeProvider: time);

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
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public async Task Cluster_mode_with_ignored_timestamp_tolerance_rejects_instead_of_in_memory_fallback(bool returnNull)
    {
        const string secret = "test-secret-key";
        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret },
            Insecure = new InsecureGettingStartedOptions { warn_ignore_webhook_timestamp_tolerance = true }
        });
        var receiver = new DbtWebhookReceiver(
            options,
            new NoopCircuitBreaker(),
            NullLogger<DbtWebhookReceiver>.Instance,
            clusterState: new FailingClusterState(returnNull),
            timeProvider: new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z")));

        var payload = """{"eventId":"evt_cluster_fail","eventType":"job_run.completed"}""";
        var res = await receiver.HandleVerifiedAsync(payload);

        res.Success.ShouldBeFalse();
        receiver.ActiveDedupCount.ShouldBe(0);
    }
}
