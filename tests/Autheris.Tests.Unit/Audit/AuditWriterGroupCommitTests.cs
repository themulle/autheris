using Autheris.Application.Audit;
using Autheris.Application.Auditing;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>WP-PF-04 / SEC-SC-08: required (Tier-A) audit writes are group-committed and acknowledged only after the batch commit.</summary>
[Trait("Category", "Security")]
public sealed class AuditWriterGroupCommitTests
{
    private static AuditLogEntry Entry(int i) => new()
    {
        EventType = AuditEventTypes.AuditConfigChanged,
        ActorSid = new Sid($"S-1-5-21-ADMIN-{i}"),
        TargetTable = "governance",
        Decision = "ALLOW",
        TraceId = $"trace-{i}"
    };

    private sealed class GatedBatchRepository : IAuditLogRepository
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<AuditLogEntry>> Batches { get; } = [];
        public int SingleWrites { get; private set; }
        public Exception? Fault { get; set; }
        public Task FirstBatchEntered => _entered.Task;
        public void Release() => _gate.TrySetResult();

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            SingleWrites++;
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
        {
            lock (Batches) Batches.Add(entries.ToList());
            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            if (Fault != null) throw Fault;
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    [Fact]
    public async Task SEC_SC_08_ConcurrentRequiredWrites_AreCoalescedIntoOneBatch_AndAckedOnlyAfterCommit()
    {
        const int n = 8;
        var repo = new GatedBatchRepository();
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = n, MaxBatchDelayMs = 2000, ChannelCapacity = 64 });
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance, sink);

        var tasks = Enumerable.Range(0, n).Select(i => writer.RecordRequiredAsync(Entry(i))).ToArray();
        await repo.FirstBatchEntered;

        // The batch is in the store but not committed yet: nobody is acknowledged.
        tasks.ShouldAllBe(t => !t.IsCompleted);

        repo.Release();
        await Task.WhenAll(tasks);

        repo.Batches.Count.ShouldBe(1);
        repo.Batches[0].Count.ShouldBe(n);
        repo.SingleWrites.ShouldBe(0);
    }

    [Fact]
    public async Task SEC_SC_08_BatchFailure_FailsEveryCaller_ClosedWithAuditUnavailable()
    {
        var repo = new GatedBatchRepository { Fault = new InvalidOperationException("disk full") };
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 3, MaxBatchDelayMs = 2000, ChannelCapacity = 16 });
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance, sink);

        var tasks = Enumerable.Range(0, 3).Select(i => writer.RecordRequiredAsync(Entry(i))).ToArray();
        await repo.FirstBatchEntered;
        repo.Release();

        foreach (var t in tasks)
        {
            await Should.ThrowAsync<AuditUnavailableException>(() => t);
        }
    }

    [Fact]
    public async Task SEC_SC_08_AnchorFailure_FromTheBatch_IsPropagatedAsAuditUnavailable()
    {
        var repo = new GatedBatchRepository { Fault = new AuditUnavailableException("anchor not persisted") };
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 1, MaxBatchDelayMs = 0, ChannelCapacity = 4 });
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance, sink);

        var t = writer.RecordRequiredAsync(Entry(1));
        await repo.FirstBatchEntered;
        repo.Release();

        await Should.ThrowAsync<AuditUnavailableException>(() => t);
    }

    [Fact]
    public async Task CallerContext_IsResolvedOnTheRequestThread_BeforeEnqueue()
    {
        var repo = new GatedBatchRepository();
        repo.Release();
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 1, MaxBatchDelayMs = 0, ChannelCapacity = 4 });
        var provider = Substitute.For<ICallerContextProvider>();
        provider.ResolveCallerContext().Returns(new CallerContext("tableau", "2026.1"));
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance, sink, provider);

        await writer.RecordRequiredAsync(Entry(1));

        repo.Batches.ShouldHaveSingleItem().ShouldHaveSingleItem().Caller!.Application.ShouldBe("tableau");
        provider.Received(1).ResolveCallerContext();
    }

    [Fact]
    public async Task BestEffortWrites_StayOnTheRepositoryPath_NotTheSink()
    {
        var repo = new GatedBatchRepository();
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 1, MaxBatchDelayMs = 0, ChannelCapacity = 4 });
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance, sink);

        await writer.RecordBestEffortAsync(Entry(1));

        repo.SingleWrites.ShouldBe(1);
        repo.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutSink_RequiredWrite_UsesTheRepositoryDirectly()
    {
        var repo = new GatedBatchRepository();
        var writer = new AuditWriter(repo, NullLogger<AuditWriter>.Instance);

        await writer.RecordRequiredAsync(Entry(1));

        repo.SingleWrites.ShouldBe(1);
    }
}
