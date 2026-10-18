using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>
/// SEC-AUD161-03 / SEC-AUD161-06 / SEC-AUD161-07: Worker unit tests covering distributed fencing,
/// bounded multi-batch execution, lease cancellation, and zero-payload Tier A self-audit emission.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditRetentionPurgeWorkerTests
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly IOptionsMonitor<AuditRetentionOptions> _optionsMonitor;
    private readonly IAuditWriter _auditWriter;
    private readonly IAuditChainAnchorStore _anchorStore;
    private readonly IAuditPurgeKeyProvider _keyProvider;
    private readonly IAuditLogRepository _repository;
    private readonly ILogger<AuditRetentionPurgeWorker> _logger;
    private readonly FakeTimeProvider _timeProvider;
    private readonly byte[] _hmacKey;

    public AuditRetentionPurgeWorkerTests()
    {
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        _scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        _repository = Substitute.For<IAuditLogRepository>();
        sp.GetService(typeof(IAuditLogRepository)).Returns(_repository);
        sp.GetService(typeof(IAuditPurgeKeyProvider)).Returns(null);

        _clusterState = Substitute.For<IDistributedClusterStateProvider>();
        _optionsMonitor = Substitute.For<IOptionsMonitor<AuditRetentionOptions>>();
        _auditWriter = Substitute.For<IAuditWriter>();
        _anchorStore = Substitute.For<IAuditChainAnchorStore>();
        _keyProvider = Substitute.For<IAuditPurgeKeyProvider>();
        _logger = Substitute.For<ILogger<AuditRetentionPurgeWorker>>();
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        _hmacKey = RandomNumberGenerator.GetBytes(32);
        _keyProvider.GetAuditHmacKey().Returns(_hmacKey);

        _optionsMonitor.CurrentValue.Returns(new AuditRetentionOptions
        {
            Enabled = true,
            RetentionDays = 90,
            BatchSize = 100,
            InterBatchDelay = TimeSpan.Zero
        });
    }

    private AuditRetentionPurgeWorker CreateWorker() => new(
        _scopeFactory,
        _clusterState,
        _optionsMonitor,
        _auditWriter,
        _anchorStore,
        _logger,
        _keyProvider,
        null,
        _timeProvider);

    [Fact]
    public async Task ExecutePurgeCycle_EligibleRows_ExecutesPurgeAndEmitsZeroPayloadAudit()
    {
        var worker = CreateWorker();
        var options = new AuditRetentionOptions
        {
            Enabled = true,
            RetentionDays = 90,
            BatchSize = 100,
            InterBatchDelay = TimeSpan.Zero
        };

        // Plan returns 50 eligible entries (sequences 1..50, first surviving 51)
        _repository.PlanPurgePrefixAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((50L, 51L, "prev-hash-51", 50L));

        _repository.ExecutePurgeBatchAsync(50L, 100, Arg.Any<CancellationToken>())
            .Returns(50);

        AuditLogEntry? emittedAudit = null;
        await _auditWriter.RecordRequiredAsync(Arg.Do<AuditLogEntry>(e => emittedAudit = e), Arg.Any<CancellationToken>());

        await worker.ExecutePurgeCycleAsync(options, CancellationToken.None);

        // Verify anchor store and DB persistence
        _anchorStore.Received(1).SavePurgeCheckpoint(Arg.Is<AuditPurgeCheckpoint>(cp =>
            cp.FirstSurvivingSequence == 51 &&
            cp.SurvivingPrevHash == "prev-hash-51" &&
            cp.PurgedCount == 50 &&
            !string.IsNullOrEmpty(cp.Signature)));

        await _repository.Received(1).SavePurgeCheckpointAsync(Arg.Any<AuditPurgeCheckpoint>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).ExecutePurgeBatchAsync(50L, 100, Arg.Any<CancellationToken>());

        // Verify Zero-Payload Tier A self-audit entry (AC-161-07, AC-161-08)
        emittedAudit.ShouldNotBeNull();
        emittedAudit.EventType.ShouldBe(AuditEventTypes.AuditRetentionPurge);
        emittedAudit.ActorSid.ShouldBe(new Sid("S-1-5-18"));
        emittedAudit.Decision.ShouldBe("PURGED");

        using var doc = JsonDocument.Parse(emittedAudit.DetailsJson);
        var root = doc.RootElement;
        root.GetProperty("purgedCount").GetInt64().ShouldBe(50);
        root.GetProperty("firstSurvivingSequence").GetInt64().ShouldBe(51);
        root.GetProperty("lastPurgedSequence").GetInt64().ShouldBe(50);
        root.TryGetProperty("query", out _).ShouldBeFalse("Zero-payload rule: no query text allowed.");
        root.TryGetProperty("actorSid", out _).ShouldBeFalse("Zero-payload rule: no user actor SID allowed.");
    }

    [Fact]
    public async Task ExecutePurgeCycle_MultiBatch_LoopsUntilAllPurged()
    {
        var worker = CreateWorker();
        var options = new AuditRetentionOptions
        {
            Enabled = true,
            RetentionDays = 90,
            BatchSize = 100,
            InterBatchDelay = TimeSpan.Zero
        };

        // 250 rows eligible
        _repository.PlanPurgePrefixAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((250L, 251L, "prev-hash-251", 250L));

        _repository.ExecutePurgeBatchAsync(250L, 100, Arg.Any<CancellationToken>())
            .Returns(100, 100, 50);

        await worker.ExecutePurgeCycleAsync(options, CancellationToken.None);

        await _repository.Received(3).ExecutePurgeBatchAsync(250L, 100, Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.DetailsJson.Contains("\"purgedCount\":250")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPurgeCycleWithLease_LeaseUnavailable_SkipsGracefully()
    {
        var worker = CreateWorker();
        var options = new AuditRetentionOptions { Enabled = true };

        _clusterState.TryAcquireLeaseAsync(AuditRetentionPurgeWorker.LeaseKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IDistributedLease?>(null));

        await worker.RunPurgeCycleWithLeaseAsync(options, CancellationToken.None);

        await _repository.DidNotReceive().PlanPurgePrefixAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPurgeCycleWithLease_LeaseLost_AbortsExecution()
    {
        var worker = CreateWorker();
        var options = new AuditRetentionOptions
        {
            Enabled = true,
            BatchSize = 50,
            InterBatchDelay = TimeSpan.Zero
        };

        var leaseCts = new CancellationTokenSource();
        var lease = Substitute.For<IDistributedLease>();
        lease.LeaseLost.Returns(leaseCts.Token);

        _clusterState.TryAcquireLeaseAsync(AuditRetentionPurgeWorker.LeaseKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IDistributedLease?>(lease));

        _repository.PlanPurgePrefixAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((500L, 501L, "prev-hash", 500L));

        _repository.ExecutePurgeBatchAsync(500L, 50, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Simulate lease loss mid-purge
                leaseCts.Cancel();
                return Task.FromException<int>(new OperationCanceledException(leaseCts.Token));
            });

        await worker.RunPurgeCycleWithLeaseAsync(options, CancellationToken.None);

        // Verification: did not record audit completion because it aborted early
        await _auditWriter.DidNotReceive().RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}
