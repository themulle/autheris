using Autheris.Application.Auditing;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Auditing;

public sealed class AuditGroupCommitSinkTests
{
    private static AuditLogEntry Entry(string trace) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default, EventType = "TABLE_QUERY", ActorSid = new Sid("user"), TargetTable = "t", Decision = "ALLOW", TraceId = trace
    };

    [Fact]
    public async Task SEC_SC_08_OneBadEntry_DoesNotFailTheOtherCallersOfTheBatch()
    {
        var repo = NSubstitute.Substitute.For<IAuditLogRepository>();
        var persisted = new System.Collections.Concurrent.ConcurrentBag<string>();
        repo.RecordAuditEventsAsync(NSubstitute.Arg.Any<IReadOnlyList<AuditLogEntry>>(), NSubstitute.Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var entries = ci.Arg<IReadOnlyList<AuditLogEntry>>();
            if (entries.Any(e => e.TraceId == "bad"))
            {
                return Task.FromException(new ArgumentException("entry cannot be persisted"));
            }

            foreach (var e in entries) persisted.Add(e.TraceId);
            return Task.CompletedTask;
        });
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 16, MaxBatchDelayMs = 100 });

        var good1 = sink.RecordAuditEventAsync(Entry("g1"));
        var bad = sink.RecordAuditEventAsync(Entry("bad"));
        var good2 = sink.RecordAuditEventAsync(Entry("g2"));

        await good1;
        await good2;
        await Should.ThrowAsync<ArgumentException>(() => bad);
        persisted.OrderBy(x => x).ShouldBe(["g1", "g2"]);
    }

    [Fact]
    public async Task SEC_SC_08_AuditUnavailable_FailsTheWholeBatch_WithoutARetryThatCouldDuplicateCommittedRows()
    {
        var repo = NSubstitute.Substitute.For<IAuditLogRepository>();
        repo.RecordAuditEventsAsync(NSubstitute.Arg.Any<IReadOnlyList<AuditLogEntry>>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Autheris.Application.Audit.AuditUnavailableException("anchor")));
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 16, MaxBatchDelayMs = 100 });

        var a = sink.RecordAuditEventAsync(Entry("a"));
        var b = sink.RecordAuditEventAsync(Entry("b"));

        await Should.ThrowAsync<Autheris.Application.Audit.AuditUnavailableException>(() => a);
        await Should.ThrowAsync<Autheris.Application.Audit.AuditUnavailableException>(() => b);
        await repo.Received(1).RecordAuditEventsAsync(NSubstitute.Arg.Any<IReadOnlyList<AuditLogEntry>>(), NSubstitute.Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SEC_SC_08_DeadDrainLoop_FailsPendingAndLaterCallersImmediately()
    {
        var repo = NSubstitute.Substitute.For<IAuditLogRepository>();
        await using var sink = new AuditGroupCommitSink(repo, new AuditGroupCommitOptions { MaxBatchSize = 16, MaxBatchDelayMs = 0 });
        sink.OnBatchCollected = _ => throw new InvalidOperationException("loop bug");

        await Should.ThrowAsync<Autheris.Application.Audit.AuditUnavailableException>(() => sink.RecordAuditEventAsync(Entry("pending")).WaitAsync(TimeSpan.FromSeconds(5)));
        await Should.ThrowAsync<Autheris.Application.Audit.AuditUnavailableException>(() => sink.RecordAuditEventAsync(Entry("later")).WaitAsync(TimeSpan.FromSeconds(5)));
        await repo.DidNotReceiveWithAnyArgs().RecordAuditEventsAsync(default!, default);
    }

    [Fact]
    public async Task RecordAuditEventAsync_CoalescesConcurrentWritesIntoBatchCommits()
    {
        var repository = new RecordingBatchAuditRepository(delayMs: 15);
        var options = new AuditGroupCommitOptions
        {
            MaxBatchSize = 16,
            MaxBatchDelayMs = 20,
            ChannelCapacity = 128
        };

        await using var sink = new AuditGroupCommitSink(repository, options);

        const int totalEntries = 12;
        var tasks = Enumerable.Range(0, totalEntries)
            .Select(i => sink.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                EventType = "TABLE_QUERY",
                ActorSid = new Sid($"user-{i}"),
                TargetTable = "public.orders",
                Decision = "ALLOW",
                TraceId = $"trace-{i}"
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        repository.PersistedEntries.Count.ShouldBe(totalEntries);
        repository.BatchCallCount.ShouldBeLessThan(totalEntries);
        repository.BatchCallCount.ShouldBeGreaterThanOrEqualTo(1);

        // Verify AuditChainEngine linked the entries sequentially without gaps
        string expectedPrev = AuditChainEngine.AuditGenesisHash;
        for (int i = 0; i < repository.PersistedEntries.Count; i++)
        {
            var persisted = repository.PersistedEntries[i];
            persisted.ChainSequence.ShouldBe(i + 1);
            persisted.PrevHash.ShouldBe(expectedPrev);
            persisted.EntryHash.ShouldNotBeNullOrWhiteSpace();
            expectedPrev = persisted.EntryHash;
        }
    }

    [Fact]
    public async Task RecordAuditEventAsync_PropagatesPersistenceExceptionToAllCallersInFailedBatch_FailClosed()
    {
        var repository = new FailingBatchAuditRepository(new InvalidOperationException("Simulated disk full in WORM audit store"));
        var options = new AuditGroupCommitOptions
        {
            MaxBatchSize = 8,
            MaxBatchDelayMs = 15,
            ChannelCapacity = 64
        };

        await using var sink = new AuditGroupCommitSink(repository, options);

        var tasks = Enumerable.Range(0, 4)
            .Select(i => sink.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                EventType = "CONSENT_GRANTED",
                ActorSid = new Sid($"admin-{i}"),
                TargetTable = "public.customers",
                Decision = "ALLOW"
            }))
            .ToArray();

        foreach (var task in tasks)
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await task);
            ex.Message.ShouldContain("Simulated disk full");
        }
    }

    private sealed class RecordingBatchAuditRepository(int delayMs) : IAuditLogRepository
    {
        private readonly object _gate = new();
        private readonly byte[] _hmacKey = new byte[32];
        private string _lastHash = AuditChainEngine.AuditGenesisHash;
        private long _lastSeq;

        public List<AuditLogEntry> PersistedEntries { get; } = [];
        public int BatchCallCount { get; private set; }

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) =>
            RecordAuditEventsAsync([entry], ct);

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) =>
            RecordAuditEventsAsync([entry], ct);

        public async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken = default)
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                BatchCallCount++;
                long startSeq = _lastSeq + 1;
                _lastHash = AuditChainEngine.ComputeBatchChain(entries, _lastHash, _hmacKey, startSeq);
                _lastSeq += entries.Count;
                PersistedEntries.AddRange(entries);
            }
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(PersistedEntries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(PersistedEntries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FailingBatchAuditRepository(Exception exceptionToThrow) : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) =>
            Task.FromException(exceptionToThrow);

        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) =>
            Task.FromException(exceptionToThrow);

        public Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken = default) =>
            Task.FromException(exceptionToThrow);

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<AuditLogEntry>>(exceptionToThrow);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<AuditLogEntry>>(exceptionToThrow);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) =>
            Task.FromException<bool>(exceptionToThrow);
    }
}
