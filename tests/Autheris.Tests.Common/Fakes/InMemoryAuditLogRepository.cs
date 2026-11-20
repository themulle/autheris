using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Security;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Passive in-memory audit store. Appended entries are recorded in append order so tests can assert state.
/// Reads honour tenant scoping like the SQL repository (a null tenant is the unscoped admin read, a value restricts
/// to that tenant). Purge, checkpoint, chain verification and cursor paging are not simulated and throw.
/// </summary>
public sealed class InMemoryAuditLogRepository : IAuditLogRepository
{
    private readonly object _gate = new();
    private readonly List<AuditLogEntry> _entries = [];

    /// <summary>Snapshot of the recorded entries in append order.</summary>
    public IReadOnlyList<AuditLogEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) { _entries.Add(entry); }
        return Task.CompletedTask;
    }

    public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default)
        => RecordAuditEventAsync(entry, ct);

    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
        => QueryAuditLogsAsync(null, null, null, limit, tenantId, ct);

    public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        IEnumerable<AuditLogEntry> q = Entries.Reverse(); // newest insertion first, like rowid DESC
        if (tenantId is { } t) { q = q.Where(e => e.TenantId == t); }
        if (targetTable is not null) { q = q.Where(e => string.Equals(e.TargetTable, targetTable, StringComparison.OrdinalIgnoreCase)); }
        if (actorSid is { } a) { q = q.Where(e => e.ActorSid == a); }
        if (since is { } s) { q = q.Where(e => e.OccurredAt >= s); }
        return Task.FromResult<IReadOnlyList<AuditLogEntry>>(q.Take(Math.Max(1, limit)).ToList());
    }

    public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryAuditLogRepository)} does not simulate the audit hash chain.");

    public Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryAuditLogRepository)} does not simulate purge checkpoints.");

    public Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryAuditLogRepository)} does not simulate purge checkpoints.");

    public Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(DateTimeOffset thresholdUtc, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryAuditLogRepository)} does not simulate audit purge.");

    public Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryAuditLogRepository)} does not simulate audit purge.");
}
