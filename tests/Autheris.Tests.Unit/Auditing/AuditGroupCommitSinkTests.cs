using Autheris.Application.Auditing;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Auditing;

public sealed class AuditGroupCommitSinkTests
{
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
                TenantId = TenantId.LegacySingleTenant,
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
                TenantId = TenantId.LegacySingleTenant,
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
