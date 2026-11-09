using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    byte[]? IAuditPurgeKeyProvider.GetAuditHmacKey() => ((IAuditPurgeKeyProvider)_governanceRepository).GetAuditHmacKey();

    internal static class AuditSql
    {
        public const string SelectLatestPurgeCheckpoint = GovernanceRepository.AuditSql.SelectLatestPurgeCheckpointPg;
        public const string InsertPurgeCheckpoint = GovernanceRepository.AuditSql.InsertPurgeCheckpointPg;
        public static readonly AuditPurgePlanStatements PurgePlan = GovernanceRepository.AuditSql.PurgePlanPg;
        public static string GetAuditLogEntries(bool withTenant) => GovernanceRepository.AuditSql.GetAuditLogEntriesPg(withTenant);
        public static string FormatQueryAuditLogs(string whereSql) => GovernanceRepository.AuditSql.FormatQueryAuditLogsPg(whereSql);
        public static string FormatQueryAuditLogPage(string wherePredicate) => GovernanceRepository.AuditSql.FormatQueryAuditLogPagePg(wherePredicate);
        public const string SelectChainRangeMinMax = GovernanceRepository.AuditSql.SelectChainRangeMinMax;
        public const string SelectChainRangeCount = GovernanceRepository.AuditSql.SelectChainRangeCount;
        public const string SelectChainPage = GovernanceRepository.AuditSql.SelectChainPagePg;
        public const string SelectTail = GovernanceRepository.AuditSql.SelectTailPg;
        public const string SelectCount = GovernanceRepository.AuditSql.SelectCount;
        public const string SelectVerifyScan = GovernanceRepository.AuditSql.SelectVerifyScan;
    }

    public Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetLatestPurgeCheckpointAsync(ct);
    }

    public Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.SavePurgeCheckpointAsync(checkpoint, ct);
    }

    public Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(
        DateTimeOffset thresholdUtc,
        CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.PlanPurgePrefixAsync(thresholdUtc, ct);
    }

    public Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ExecutePurgeBatchAsync(maxSequenceInclusive, batchSize, ct);
    }

    public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RecordAuditEventAsync(entry, ct);
    }

    public Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RecordAuditEventsAsync(entries, ct);
    }

    public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RecordAuditEventAsync(entry, existingTx, ct);
    }

    internal Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct, DbTransaction? existingTx = null)
    {
        ThrowIfNotReady();
        return _governanceRepository.RecordAuditEventInternalAsync(entry, ct, existingTx);
    }

    public void OnTransactionCommitted(DbTransaction tx) =>
        _governanceRepository.OnTransactionCommitted(tx);

    public void OnTransactionRolledBack(DbTransaction tx) =>
        _governanceRepository.OnTransactionRolledBack(tx);

    public void RollbackPendingAuditTransactions() =>
        _governanceRepository.RollbackPendingAuditTransactions();

    public Task FlushAuditChannelAsync(CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.FlushAuditChannelAsync(ct);
    }

    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetAuditLogEntriesAsync(limit, tenantId, ct);
    }

    public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.QueryAuditLogsAsync(targetTable, actorSid, since, limit, tenantId, ct);
    }

    public Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.QueryAuditLogPageAsync(query, ct);
    }

    public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.VerifyAuditHashChainAsync(ct);
    }

    public Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetAuditChainRangeAsync(windowFrom, windowTo, ct);
    }

    public Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetAuditChainPageAsync(afterRowId, lastRowIdInclusive, pageSize, ct);
    }

    public AuditChainAnchor? GetVerifiedChainAnchor()
    {
        ThrowIfNotReady();
        return _governanceRepository.GetVerifiedChainAnchor();
    }
}
