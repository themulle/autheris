using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    byte[]? IAuditPurgeKeyProvider.GetAuditHmacKey() => _auditHmacKey;

    internal static class AuditSql
    {
        public const string SelectLatestPurgeCheckpoint = @"SELECT first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature
                            FROM AUDIT_PURGE_CHECKPOINTS
                            ORDER BY first_surviving_seq DESC
                            LIMIT 1";

        public const string InsertPurgeCheckpoint = @"INSERT INTO AUDIT_PURGE_CHECKPOINTS
                            (id, first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature)
                            VALUES (@id, @firstSeq, @prevHash, @purgedCount, @purgedThrough, @createdAt, @sig, @extSig)";

        public static readonly AuditPurgePlanStatements PurgePlan = new(
            SelectOldestRowSql: "SELECT seq, occurred_at FROM AUDIT_LOG_ENTRIES ORDER BY seq ASC LIMIT 1",
            SelectSurvivingRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES WHERE occurred_at >= @threshold ORDER BY seq ASC LIMIT 1",
            SelectNewestRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq DESC LIMIT 1",
            SelectMaxEligibleSeqSql: "SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES WHERE seq < @survivingSeq");

        public static string GetAuditLogEntries(bool withTenant) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                            FROM AUDIT_LOG_ENTRIES
                            {(withTenant ? " WHERE tenant_id = @tenantId" : "")}
                            ORDER BY rowid DESC
                            LIMIT @lim";

        public static string FormatQueryAuditLogs(string whereSql) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                            FROM AUDIT_LOG_ENTRIES
                            {whereSql}
                            ORDER BY rowid DESC
                            LIMIT @lim";

        public static string FormatQueryAuditLogPage(string wherePredicate) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                            FROM AUDIT_LOG_ENTRIES
                            WHERE {wherePredicate}
                            ORDER BY seq DESC
                            LIMIT @lim";

        public const string SelectChainRangeMinMax = @"SELECT MIN(rowid), MAX(rowid)
                            FROM AUDIT_LOG_ENTRIES
                            WHERE occurred_at >= @from AND occurred_at < @to";

        public const string SelectChainRangeCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";

        public const string SelectChainPage = @"SELECT rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column,
                                   decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            WHERE rowid > @after AND rowid <= @last
                            ORDER BY rowid ASC
                            LIMIT @lim";

        public const string SelectTail = "SELECT entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";

        public const string SelectCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";

        public const string SelectVerifyScan = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, seq, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            ORDER BY rowid ASC";
    }

    public async Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetLatestPurgeCheckpointAsync(conn, AuditSql.SelectLatestPurgeCheckpoint, ct).ConfigureAwait(false);
    }

    public async Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);

        _auditAnchorStore?.SavePurgeCheckpoint(checkpoint);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await AuditSqlCore.SavePurgeCheckpointAsync(conn, AuditSql.InsertPurgeCheckpoint, checkpoint, ct).ConfigureAwait(false);
    }

    public async Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(
        DateTimeOffset thresholdUtc,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.PlanPurgePrefixAsync(conn, AuditSql.PurgePlan, thresholdUtc, ct).ConfigureAwait(false);
    }

    public async Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var authCmd = conn.CreateCommand())
            {
                authCmd.Transaction = tx;
                authCmd.CommandText = "SELECT set_config('autheris.audit_purge_authorized', 'true', true);";
                await authCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int rowsPurged;
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = @"DELETE FROM AUDIT_LOG_ENTRIES
                                      WHERE ctid IN (
                                          SELECT ctid FROM AUDIT_LOG_ENTRIES
                                          WHERE seq <= @maxSeq
                                          ORDER BY seq ASC
                                          LIMIT @batchSize
                                      )";
                delCmd.Parameters.AddWithValue("@maxSeq", maxSequenceInclusive);
                delCmd.Parameters.AddWithValue("@batchSize", batchSize);
                rowsPurged = await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return rowsPurged;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        StoredTenant.RequireWritable(entry.TenantId); // DT-SC-04: a missing tenant is refused at the door, not dead-lettered later
        // Tier-B events are only enqueued (no connection); they must not be accepted before the audit processor runs.
        ThrowIfNotReady();

        if (entry.Caller == null && _callerContextProvider != null)
        {
            entry.Caller = _callerContextProvider.ResolveCallerContext();
        }

        // Tier-A: Security critical mutations, admin actions, or denied requests are synchronous (Fail-Closed)
        bool isTierA = AuditChainViolationGate.IsTierA(entry);

        // SR-L-08: a latched chain violation is applied per policy to both tiers (Tier-A is marked, Tier-B refused by default).
        var violation = Volatile.Read(ref _auditChainViolation);
        var admitted = AuditChainViolationGate.Admit(entry, isTierA, violation, ViolationPolicy, _logger);

        // SEC R2-3: Audit:SynchronousQueryAudit commits query events before the request is answered (no crash window).
        if (isTierA || _options?.Audit?.SynchronousQueryAudit == true)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecordAuditEventInternalAsync(admitted, ct).ConfigureAwait(false);
                if (!ReferenceEquals(admitted, entry))
                {
                    entry.PrevHash = admitted.PrevHash;
                    entry.EntryHash = admitted.EntryHash;
                }

                if (violation != null && isTierA && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
                {
                    await EmitChainFaultEventOnceAsync(admitted, violation, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _auditLock.Release();
            }
            return;
        }

        if (_isAuditPipelineFaulted)
        {
            _logger?.LogError("Audit pipeline is faulted; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new AuditUnavailableException("Audit pipeline is in a faulted state (fail-closed).");
        }

        // Tier-B: High-volume query reads are enqueued to the bounded channel with timeout (fail-closed)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _auditChannel.Writer.WriteAsync(entry, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError("Audit channel saturated; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new AuditUnavailableException("Audit pipeline saturated (fail-closed).");
        }
    }

    public async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var candidate in entries ?? throw new ArgumentNullException(nameof(entries)))
        {
            StoredTenant.RequireWritable(candidate.TenantId); // DT-SC-04
        }

        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        ThrowIfNotReady();

        var violation = Volatile.Read(ref _auditChainViolation);
        var admittedBatch = new List<AuditLogEntry>(entries.Count);
        AuditLogEntry? firstTierA = null;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Caller == null && _callerContextProvider != null)
            {
                entry.Caller = _callerContextProvider.ResolveCallerContext();
            }

            bool isTierA = AuditChainViolationGate.IsTierA(entry);
            var admitted = AuditChainViolationGate.Admit(entry, isTierA, violation, ViolationPolicy, _logger);
            admittedBatch.Add(admitted);
            if (isTierA && firstTierA == null)
            {
                firstTierA = admitted;
            }
        }

        await _auditLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecordAuditEventsBatchInternalAsync(admittedBatch, ct).ConfigureAwait(false);
            for (int i = 0; i < entries.Count; i++)
            {
                if (!ReferenceEquals(admittedBatch[i], entries[i]))
                {
                    entries[i].PrevHash = admittedBatch[i].PrevHash;
                    entries[i].EntryHash = admittedBatch[i].EntryHash;
                    entries[i].ChainSequence = admittedBatch[i].ChainSequence;
                }
            }

            if (violation != null && firstTierA != null && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
            {
                await EmitChainFaultEventOnceAsync(firstTierA, violation, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _auditLock.Release();
        }
    }

    private AuditChainViolationPolicy ViolationPolicy => AuditChainViolationGate.ResolvePolicy(_options?.Audit, _isDevOrTest);

    private int _auditFaultEventEmitted;

    /// <summary>SR-L-08: writes exactly one <c>AUDIT_PIPELINE_FAULT</c> event per latch. Caller holds the audit write lock.</summary>
    private async Task EmitChainFaultEventOnceAsync(AuditLogEntry trigger, string violation, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _auditFaultEventEmitted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await RecordAuditEventInternalAsync(AuditChainViolationGate.CreateFaultEvent(trigger, violation, ViolationPolicy), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to write the AUDIT_PIPELINE_FAULT event for the latched audit chain violation.");
        }
    }

    public async Task FlushAuditChannelAsync(CancellationToken ct = default)
    {
        ThrowIfNotReady();
        var batch = new List<AuditLogEntry>();
        while (_auditChannel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250)
            {
                await _auditLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await RecordAuditEventsBatchInternalAsync(batch, ct).ConfigureAwait(false);
                }
                finally
                {
                    _auditLock.Release();
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecordAuditEventsBatchInternalAsync(batch, ct).ConfigureAwait(false);
            }
            finally
            {
                _auditLock.Release();
            }
        }
    }

    private async Task ProcessAuditChannelAsync()
    {
        var batch = new List<AuditLogEntry>(250);
        var reader = _auditChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_auditCts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                var deadline = DateTime.UtcNow.AddMilliseconds(10);

                while (batch.Count < 250 && (DateTime.UtcNow < deadline || batch.Count == 0))
                {
                    if (reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                    }
                    else
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    const int maxRetries = 3;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        await _auditLock.WaitAsync(_auditCts.Token).ConfigureAwait(false);
                        try
                        {
                            await RecordAuditEventsBatchInternalAsync(batch, _auditCts.Token).ConfigureAwait(false);
                            if (_isAuditPipelineFaulted)
                            {
                                _isAuditPipelineFaulted = false;
                                _logger?.LogInformation("AU-05: Audit pipeline recovered successfully. Fault flag cleared.");
                            }
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries (attempt {Attempt}/{MaxRetries})", batch.Count, attempt, maxRetries);
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(50 * attempt, _auditCts.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _isAuditPipelineFaulted = true;
                                _logger?.LogCritical(ex, "FATAL: Audit batch of {Count} entries failed permanently after 3 attempts. Setting audit pipeline to faulted (fail-closed). Diverting to dead-letter queue.", batch.Count);
                                await WriteToDeadLetterAsync(batch, ex.Message, _auditCts.Token).ConfigureAwait(false);
                                Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
                            }
                        }
                        finally
                        {
                            _auditLock.Release();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining on shutdown
        }

        batch.Clear();
        while (reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250)
            {
                await _auditLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await RecordAuditEventsBatchInternalAsync(batch, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries during shutdown drain; diverting to dead-letter queue.", batch.Count);
                    await WriteToDeadLetterAsync(batch, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
                }
                finally
                {
                    _auditLock.Release();
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _auditLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await RecordAuditEventsBatchInternalAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries during shutdown drain; diverting to dead-letter queue.", batch.Count);
                await WriteToDeadLetterAsync(batch, ex.Message, CancellationToken.None).ConfigureAwait(false);
                Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
            }
            finally
            {
                _auditLock.Release();
            }
        }
    }

    private async Task WriteToDeadLetterAsync(IReadOnlyList<AuditLogEntry> batch, string errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO AUDIT_DEAD_LETTER (id, batch_json, error_message, failed_at, tenant_id)
                                VALUES (@id, @batch, @err, @failed, @tenant)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@batch", JsonSerializer.Serialize(batch));
            cmd.Parameters.AddWithValue("@err", errorMessage);
            cmd.Parameters.AddWithValue("@failed", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@tenant", batch.FirstOrDefault()?.TenantId.Value ?? TenantId.Platform.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception dlEx)
        {
            _logger?.LogCritical(dlEx, "Failed to write audit batch to AUDIT_DEAD_LETTER table!");
        }
    }

    private const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        if (existingTx == null)
        {
            return RecordAuditEventAsync(entry, ct);
        }
        return RecordAuditEventInternalAsync(entry, ct, existingTx);
    }

    internal Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct, System.Data.Common.DbTransaction? existingTx = null) =>
        RecordAuditEventsBatchInternalAsync([entry], ct, existingTx);

    private sealed class AuditTxPendingState
    {
        public required long LastSeq { get; set; }
        public required string LastHash { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<System.Data.Common.DbTransaction, AuditTxPendingState> _pendingAuditTxStates = new();

    public void OnTransactionCommitted(System.Data.Common.DbTransaction tx)
    {
        Exception? anchorFailure = null;
        if (_pendingAuditTxStates.TryRemove(tx, out var state))
        {
            lock (_pendingAuditTxStates)
            {
                if (state.LastSeq >= _lastAuditSeq)
                {
                    _lastAuditSeq = state.LastSeq;
                    _lastAuditHash = state.LastHash;
                }
            }

            if (Volatile.Read(ref _auditChainViolation) == null && _auditAnchorStore != null)
            {
                try
                {
                    // SR-L-07: same monotonic check as the batch path; replicas sharing one anchor store must never move it backwards.
                    var anchor = CreateSignedAnchor(state.LastSeq, state.LastHash);
                    var current = TryLoadVerifiedAnchor(out var anchorInvalid);
                    if (anchorInvalid)
                    {
                        // PERS-18: an invalid stored anchor faults the write: the chain is latched and the Tier-A event is not acknowledged.
                        FlagAuditChainViolation("Stored audit chain anchor is invalid; Tier-A writes are refused (fail-closed).");
                        throw new InvalidOperationException("The stored audit chain anchor is invalid.");
                    }

                    if (current == null || current.Sequence < anchor.Sequence)
                    {
                        _auditAnchorStore.Save(anchor);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist external audit chain anchor (seq {Sequence}).", state.LastSeq);
                    anchorFailure = ex;
                }
            }
        }

        // M1: the rows are committed, but the Tier-A mutation (consent grant/revoke, manifest apply) is not acknowledged without its anchor.
        if (anchorFailure != null)
        {
            throw new AuditUnavailableException("The audit chain anchor could not be persisted; the change is not acknowledged (fail-closed).", anchorFailure);
        }
    }

    public void OnTransactionRolledBack(System.Data.Common.DbTransaction tx)
    {
        // SR-M-23: in-memory chain state is only advanced on commit, so a rollback has nothing to restore.
        _pendingAuditTxStates.TryRemove(tx, out _);
    }

    public void RollbackPendingAuditTransactions()
    {
        _pendingAuditTxStates.Clear();
    }

    /// <summary>Review PG-1: constant key of the transaction-scoped advisory lock that serialises audit chain writers across replicas.</summary>
    private const long AuditChainAdvisoryLockKey = 0x41555448_41554454L; // "AUTH" "AUDT"

    private const int AuditWriteMaxAttempts = 5;

    private async Task RecordAuditEventsBatchInternalAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct, System.Data.Common.DbTransaction? existingTx = null)
    {
        if (batch.Count == 0) return;

        if (existingTx != null)
        {
            await WriteAuditBatchEnrolledAsync(batch, existingTx, ct).ConfigureAwait(false);
            return;
        }

        // Review PG-1: concurrent writers (other replicas) are serialised by an advisory lock; serialization failures,
        // deadlocks and unique violations on seq are retried instead of failing the mutation or faulting the pipeline.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAuditBatchOnceAsync(batch, ct).ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (attempt < AuditWriteMaxAttempts &&
                                               (ex.SqlState == PostgresErrorCodes.SerializationFailure ||
                                                ex.SqlState == PostgresErrorCodes.DeadlockDetected ||
                                                ex.SqlState == PostgresErrorCodes.UniqueViolation))
            {
                _logger?.LogWarning(ex, "Audit chain write conflict (attempt {Attempt}/{Max}); retrying.", attempt, AuditWriteMaxAttempts);
                await Task.Delay(Random.Shared.Next(10, 40) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAuditBatchEnrolledAsync(IReadOnlyList<AuditLogEntry> batch, System.Data.Common.DbTransaction existingTx, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)existingTx.Connection!;
        var tx = (NpgsqlTransaction)existingTx;

        if (!_pendingAuditTxStates.TryGetValue(existingTx, out var pendingState))
        {
            // SR-M-23: serialise with every other chain writer (transaction-scoped, released on commit/rollback) and read the
            // tail from the DB under the lock; the in-memory tail is not trustworthy before commit.
            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@k)";
                lockCmd.Parameters.AddWithValue("@k", AuditChainAdvisoryLockKey);
                await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var (dbTailHash, dbTailSeq) = ReadAuditTail(tx);
            pendingState = _pendingAuditTxStates.GetOrAdd(existingTx, _ => new AuditTxPendingState
            {
                LastSeq = dbTailSeq,
                LastHash = dbTailHash ?? AuditGenesisHash
            });
        }

        long lastSequence = pendingState.LastSeq;
        string lastEntryHash = pendingState.LastHash;

        // hashes are computed in memory from the single tail read; the rows are then written with multi-row inserts.
        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;

            var effectiveTenantId = StoredTenant.RequireWritable(entry.TenantId);

            var entryHash = ComputeAuditEntryHash(
                currentSequence,
                entry.Id.ToString(),
                currentPrevHash,
                entry.OccurredAt,
                entry.EventType,
                entry.ActorSid.Value,
                entry.TargetTable,
                entry.TargetColumn,
                entry.Decision,
                entry.TraceId,
                entry.DetailsJson,
                effectiveTenantId);

            entry.PrevHash = currentPrevHash;
            entry.EntryHash = entryHash;
            rows.Add(new AuditInsertRow(entry.Id.ToString(), AuditTimestamp.ToStorage(entry.OccurredAt), entry.EventType, entry.ActorSid.Value, entry.TargetTable,
                entry.TargetColumn, entry.Decision, entry.TraceId, entry.DetailsJson, currentPrevHash, entryHash, effectiveTenantId, currentSequence));
            lastEntryHash = entryHash;
        }

        await InsertAuditRowsAsync(conn, tx, rows, ct).ConfigureAwait(false);

        // In-memory _lastAuditSeq/_lastAuditHash are advanced in OnTransactionCommitted only.
        pendingState.LastSeq = lastSequence;
        pendingState.LastHash = lastEntryHash;
    }

    private static async Task InsertAuditRowsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<AuditInsertRow> rows, CancellationToken ct)
    {
        for (int offset = 0; offset < rows.Count; offset += AuditBatchInsertSql.MaxRowsPerStatement)
        {
            int count = Math.Min(AuditBatchInsertSql.MaxRowsPerStatement, rows.Count - offset);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = AuditBatchInsertSql.Build(count);
            for (int k = 0; k < count; k++)
            {
                var r = rows[offset + k];
                cmd.Parameters.Add($"@id{k}", NpgsqlDbType.Text).Value = r.Id;
                cmd.Parameters.Add($"@occ{k}", NpgsqlDbType.Text).Value = r.OccurredAt;
                cmd.Parameters.Add($"@event{k}", NpgsqlDbType.Text).Value = r.EventType;
                cmd.Parameters.Add($"@actor{k}", NpgsqlDbType.Text).Value = r.ActorSid;
                cmd.Parameters.Add($"@target{k}", NpgsqlDbType.Text).Value = r.TargetTable;
                cmd.Parameters.Add($"@col{k}", NpgsqlDbType.Text).Value = (object?)r.TargetColumn ?? DBNull.Value;
                cmd.Parameters.Add($"@dec{k}", NpgsqlDbType.Text).Value = r.Decision;
                cmd.Parameters.Add($"@trace{k}", NpgsqlDbType.Text).Value = r.TraceId;
                cmd.Parameters.Add($"@det{k}", NpgsqlDbType.Text).Value = r.DetailsJson;
                cmd.Parameters.Add($"@prev{k}", NpgsqlDbType.Text).Value = r.PrevHash;
                cmd.Parameters.Add($"@hash{k}", NpgsqlDbType.Text).Value = r.EntryHash;
                cmd.Parameters.Add($"@tenantId{k}", NpgsqlDbType.Text).Value = r.TenantId;
                cmd.Parameters.Add($"@seq{k}", NpgsqlDbType.Bigint).Value = r.Seq;
            }

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task WriteAuditBatchOnceAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct)
    {
        var violationAtStart = Volatile.Read(ref _auditChainViolation); // M1: only a violation latched DURING this write blocks the acknowledgement
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // READ COMMITTED + advisory lock: the tail is read only after the previous writer has committed. (Under
        // SERIALIZABLE the snapshot would already be taken before the lock is acquired.)
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@k)";
            lockCmd.Parameters.AddWithValue("@k", AuditChainAdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The DB is the single source of truth for the tail. Another replica having advanced the chain is normal; a tail
        // that is BEHIND what this process has already seen (or the same sequence with another hash) means truncation or
        // rewriting, unless a signed anchor explains it.
        var (dbTailHash, dbTailSeq) = ReadAuditTail(tx);
        var effectiveDbHash = dbTailHash ?? AuditGenesisHash;
        long knownSeq;
        string knownHash;
        lock (_pendingAuditTxStates)
        {
            knownSeq = _lastAuditSeq;
            knownHash = _lastAuditHash;
        }
        if (dbTailSeq < knownSeq || (dbTailSeq == knownSeq && !FixedTimeEqualsString(effectiveDbHash, knownHash)))
        {
            FlagAuditChainViolation(
                $"Audit chain tail in DB (seq {dbTailSeq}) is behind or differs from the last known tail (seq {knownSeq}) - possible truncation or rewrite.");
        }

        long lastSequence = dbTailSeq;
        string lastEntryHash = effectiveDbHash;

        // hashes are computed in memory from the single tail read; the rows are then written with multi-row inserts.
        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;

            var effectiveTenantId = StoredTenant.RequireWritable(entry.TenantId);

            var entryHash = ComputeAuditEntryHash(
                currentSequence,
                entry.Id.ToString(),
                currentPrevHash,
                entry.OccurredAt,
                entry.EventType,
                entry.ActorSid.Value,
                entry.TargetTable,
                entry.TargetColumn,
                entry.Decision,
                entry.TraceId,
                entry.DetailsJson,
                effectiveTenantId);

            entry.PrevHash = currentPrevHash;
            entry.EntryHash = entryHash;
            rows.Add(new AuditInsertRow(entry.Id.ToString(), AuditTimestamp.ToStorage(entry.OccurredAt), entry.EventType, entry.ActorSid.Value, entry.TargetTable,
                entry.TargetColumn, entry.Decision, entry.TraceId, entry.DetailsJson, currentPrevHash, entryHash, effectiveTenantId, currentSequence));
            lastEntryHash = entryHash;
        }

        await InsertAuditRowsAsync(conn, tx, rows, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        lock (_pendingAuditTxStates)
        {
            if (lastSequence >= _lastAuditSeq)
            {
                _lastAuditSeq = lastSequence;
                _lastAuditHash = lastEntryHash;
            }
        }

        Exception? anchorFailure = null;
        if (Volatile.Read(ref _auditChainViolation) == null && _auditAnchorStore != null)
        {
            var anchor = CreateSignedAnchor(lastSequence, lastEntryHash);
            try
            {
                // Monotonic: replicas sharing one anchor store must never move the anchor backwards.
                var current = TryLoadVerifiedAnchor(out var anchorInvalid);
                if (anchorInvalid)
                {
                    // PERS-18: an invalid stored anchor faults the write: the chain is latched and the Tier-A event is not acknowledged.
                    FlagAuditChainViolation("Stored audit chain anchor is invalid; Tier-A writes are refused (fail-closed).");
                    throw new InvalidOperationException("The stored audit chain anchor is invalid.");
                }

                if (current == null || current.Sequence < anchor.Sequence)
                {
                    _auditAnchorStore.Save(anchor);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist external audit chain anchor at seq {Sequence}.", anchor.Sequence);
                anchorFailure = ex;
            }
        }

        // a batch containing a Tier-A event is acknowledged only after its anchor is written. The rows are
        // committed and the next batch anchors the tail, but the caller fails closed. Tier-B-only batches never fail on a late anchor.
        // M1: a chain violation latched DURING this write means no anchor was written; the Tier-A event is then not acknowledged (a violation latched before admission follows the configured policy: Tier-A is written marked).
        if (anchorFailure == null && violationAtStart == null && Volatile.Read(ref _auditChainViolation) != null && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
        {
            anchorFailure = new InvalidOperationException("The audit hash chain is latched as violated; no anchor was written.");
        }

        if (anchorFailure != null && AuditChainViolationGate.AnyTierA(batch))
        {
            throw new AuditUnavailableException("The audit chain anchor could not be persisted; the event is not acknowledged (fail-closed).", anchorFailure);
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditLogEntriesAsync(conn, AuditSql.GetAuditLogEntries(tenantId != null), limit, tenantId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.QueryAuditLogsAsync(conn, AuditSql.FormatQueryAuditLogs, targetTable, actorSid, since, limit, tenantId, ct).ConfigureAwait(false);
    }

    private static AuditLogEntry ReadAuditEntry(NpgsqlDataReader reader, int o = 0) =>
        AuditSqlCore.ReadAuditEntry(reader, o);

    public async Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.QueryAuditLogPageAsync(conn, AuditSql.FormatQueryAuditLogPage, query, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        if (_auditChainViolation != null)
        {
            return false;
        }

        AuditPurgeCheckpoint? checkpoint = TryLoadVerifiedPurgeCheckpoint(out bool checkpointInvalid);
        if (checkpointInvalid)
        {
            FlagAuditChainViolation("External audit purge checkpoint has an invalid signature or format.");
            return false;
        }

        AuditChainAnchor? anchor = TryLoadVerifiedAnchor(out bool anchorInvalid);
        if (anchorInvalid)
        {
            // PERS-18: an invalid stored anchor latches the violation (as on SQLite) instead of only failing this verification.
            FlagAuditChainViolation("External audit chain anchor has an invalid signature or format.");
            return false;
        }

        long knownSeq;
        string knownHash;
        await _auditLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_pendingAuditTxStates)
            {
                knownSeq = _lastAuditSeq;
                knownHash = _lastAuditHash;
            }
        }
        finally
        {
            _auditLock.Release();
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = AuditSql.SelectVerifyScan;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = await AuditSqlCore.VerifyAuditRowsAsync(
            reader,
            checkpoint,
            anchor,
            knownSeq,
            knownHash,
            _auditHmacKey,
            FlagAuditChainViolation,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return false;
        }

        if (result.Position > 0)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_pendingAuditTxStates)
                {
                    if (result.Position > _lastAuditSeq)
                    {
                        _lastAuditSeq = result.Position;
                        _lastAuditHash = result.FinalHash;
                    }
                }
            }
            finally
            {
                _auditLock.Release();
            }
        }

        return true;
    }

    public async Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditChainRangeAsync(conn, AuditSql.SelectChainRangeMinMax, AuditSql.SelectChainRangeCount, windowFrom, windowTo, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditChainPageAsync(conn, AuditSql.SelectChainPage, afterRowId, lastRowIdInclusive, pageSize, ct).ConfigureAwait(false);
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    private (string? Hash, long Sequence) ReadAuditTail(NpgsqlTransaction? tx)
    {
        var conn = tx?.Connection ?? _dataSource.OpenConnection();
        bool ownsConn = tx == null;
        try
        {
            return AuditSqlCore.ReadAuditTail(conn, tx, AuditSql.SelectTail, AuditSql.SelectCount);
        }
        finally
        {
            if (ownsConn)
            {
                conn.Dispose();
            }
        }
    }

    private string ComputeAuditEntryHash(
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId) =>
        AuditSqlCore.ComputeAuditEntryHash(
            _auditHmacKey,
            sequence,
            id,
            prevHash,
            occurredAt,
            eventType,
            actorSid,
            targetTable,
            targetColumn,
            decision,
            traceId,
            detailsJson,
            tenantId);

    private AuditChainAnchor CreateSignedAnchor(long sequence, string entryHash) =>
        AuditSqlCore.CreateSignedAnchor(_auditAnchorKey, sequence, entryHash);

    private AuditChainAnchor? TryLoadVerifiedAnchor(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedAnchor(_auditAnchorStore, _auditAnchorKey, _logger, out invalid);

    private AuditPurgeCheckpoint? TryLoadVerifiedPurgeCheckpoint(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedPurgeCheckpoint(_auditAnchorStore, _auditHmacKey, _logger, out invalid);

    private void InitializeAuditChainAnchor()
    {
        if (_auditAnchorStore == null) return;
        if (!_isDevOrTest && _auditAnchorStore is InMemoryAuditChainAnchorStore)
        {
            _logger?.LogWarning("Audit chain anchor is only held in memory (no tamper protection across restarts); configure Audit:ChainAnchorPath on a separate, shared volume or WORM store.");
        }

        var anchor = TryLoadVerifiedAnchor(out bool invalid);
        if (invalid)
        {
            FlagAuditChainViolation("External signed audit anchor signature verification failed at startup.");
            return;
        }

        // Another replica may have written since the schema init read the tail; re-read it after the anchor was loaded.
        var (freshHash, freshSeq) = ReadAuditTail(null);
        if (freshHash != null && freshSeq > Interlocked.Read(ref _lastAuditSeq))
        {
            _lastAuditHash = freshHash;
            Interlocked.Exchange(ref _lastAuditSeq, freshSeq);
        }

        if (anchor == null)
        {
            if (_lastAuditSeq > 0)
            {
                // Review PG-2 (as SQLite): entries without an anchor outside Development indicate deletion of the
                // anchor, unless the store is only held in memory (then there is nothing that could have survived a restart).
                if (!_isDevOrTest && _auditAnchorStore is not InMemoryAuditChainAnchorStore)
                {
                    FlagAuditChainViolation(
                        $"Audit DB has {_lastAuditSeq} entries, but the external audit chain anchor is missing. Potential truncation or unauthorized deletion detected.");
                    return;
                }

                var newAnchor = CreateSignedAnchor(_lastAuditSeq, _lastAuditHash);
                try
                {
                    _auditAnchorStore.Save(newAnchor);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to seed initial external anchor for existing audit chain at seq {Sequence}.", _lastAuditSeq);
                }
            }
            return;
        }

        if (_lastAuditSeq < anchor.Sequence)
        {
            FlagAuditChainViolation(
                $"Audit chain truncated! External verified anchor has sequence {anchor.Sequence}, but database only reaches {_lastAuditSeq}.");
        }
        else if (_lastAuditSeq == anchor.Sequence && !FixedTimeEqualsString(_lastAuditHash, anchor.EntryHash))
        {
            FlagAuditChainViolation(
                $"Audit chain tail mutated! Sequence {anchor.Sequence} matches external anchor, but hash '{_lastAuditHash}' does not match anchored '{anchor.EntryHash}'.");
        }
    }

    private void FlagAuditChainViolation(string reason)
    {
        // SR-L-08: the violation is latched in its own field and never cleared; it deliberately does not touch the recoverable
        // write-failure flag, so a successful retry of the batch writer cannot mask it.
        if (Interlocked.CompareExchange(ref _auditChainViolation, reason, null) == null)
        {
            _logger?.LogCritical("AUDIT CHAIN INTEGRITY VIOLATION: {Reason}", reason);
        }
    }

    private static string EscapeField(string? v) => Autheris.Domain.Audit.AuditCanonicalizer.CanonicalizeField(v);

    private static bool FixedTimeEqualsString(string? a, string? b) =>
        AuditSqlCore.FixedTimeEqualsString(a, b);
}
