using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
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

    public async Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature
                            FROM AUDIT_PURGE_CHECKPOINTS
                            ORDER BY first_surviving_seq DESC
                            LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var firstSeq = reader.GetInt64(0);
            var prevHash = reader.GetString(1);
            var purgedCount = reader.GetInt64(2);
            var purgedThroughUtc = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            var createdAtUtc = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
            var sig = reader.GetString(5);
            var extSig = reader.IsDBNull(6) ? null : reader.GetString(6);
            return new AuditPurgeCheckpoint(firstSeq, prevHash, purgedCount, purgedThroughUtc, createdAtUtc, sig, extSig);
        }

        return null;
    }

    public async Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);

        _auditAnchorStore?.SavePurgeCheckpoint(checkpoint);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO AUDIT_PURGE_CHECKPOINTS
                            (id, first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature)
                            VALUES (@id, @firstSeq, @prevHash, @purgedCount, @purgedThrough, @createdAt, @sig, @extSig)";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@firstSeq", checkpoint.FirstSurvivingSequence);
        cmd.Parameters.AddWithValue("@prevHash", checkpoint.SurvivingPrevHash);
        cmd.Parameters.AddWithValue("@purgedCount", checkpoint.PurgedCount);
        cmd.Parameters.AddWithValue("@purgedThrough", checkpoint.PurgedThroughUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@createdAt", checkpoint.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@sig", checkpoint.Signature);
        cmd.Parameters.AddWithValue("@extSig", (object?)checkpoint.ExternalSignature ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(
        DateTimeOffset thresholdUtc,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var thresholdIso = thresholdUtc.ToUniversalTime().ToString("O");

        // 1. Check oldest row
        await using (var minCmd = conn.CreateCommand())
        {
            minCmd.CommandText = "SELECT seq, occurred_at FROM AUDIT_LOG_ENTRIES ORDER BY seq ASC LIMIT 1";
            await using var minReader = await minCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await minReader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var oldestOccurredAt = minReader.GetString(1);
            if (string.CompareOrdinal(oldestOccurredAt, thresholdIso) >= 0)
            {
                return null;
            }
        }

        // 2. Find first surviving entry (earliest entry with occurred_at >= thresholdIso)
        long survivingSeq;
        string survivingPrevHash;
        await using (var survCmd = conn.CreateCommand())
        {
            survCmd.CommandText = "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES WHERE occurred_at >= @threshold ORDER BY seq ASC LIMIT 1";
            survCmd.Parameters.AddWithValue("@threshold", thresholdIso);
            await using var survReader = await survCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await survReader.ReadAsync(ct).ConfigureAwait(false))
            {
                survivingSeq = survReader.GetInt64(0);
                survivingPrevHash = survReader.GetString(1);
            }
            else
            {
                await using var newestCmd = conn.CreateCommand();
                newestCmd.CommandText = "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq DESC LIMIT 1";
                await using var newestReader = await newestCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await newestReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
                survivingSeq = newestReader.GetInt64(0);
                survivingPrevHash = newestReader.GetString(1);
            }
        }

        // 3. Find max eligible sequence (< survivingSeq)
        long maxEligibleSeq;
        await using (var maxCmd = conn.CreateCommand())
        {
            maxCmd.CommandText = "SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES WHERE seq < @survivingSeq";
            maxCmd.Parameters.AddWithValue("@survivingSeq", survivingSeq);
            var maxObj = await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (maxObj == null || maxObj is DBNull)
            {
                return null;
            }
            maxEligibleSeq = Convert.ToInt64(maxObj, CultureInfo.InvariantCulture);
        }

        long purgeCount = survivingSeq - 1;
        return (maxEligibleSeq, survivingSeq, survivingPrevHash, purgeCount);
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
            cmd.Parameters.AddWithValue("@tenant", batch.FirstOrDefault()?.TenantId.Value ?? "unknown");
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
                    if (!anchorInvalid && (current == null || current.Sequence < anchor.Sequence))
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

        // WP-PF-04: hashes are computed in memory from the single tail read; the rows are then written with multi-row inserts.
        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;

            var effectiveTenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value) ? TenantId.LegacySingleTenant.Value : entry.TenantId.Value;

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

        // WP-PF-04: hashes are computed in memory from the single tail read; the rows are then written with multi-row inserts.
        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;

            var effectiveTenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value) ? TenantId.LegacySingleTenant.Value : entry.TenantId.Value;

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
                if (!anchorInvalid && (current == null || current.Sequence < anchor.Sequence))
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

        // WP-PF-04 / SEC-SC-08: a batch containing a Tier-A event is acknowledged only after its anchor is written. The rows are
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
        var list = new List<AuditLogEntry>();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var tenantFilter = tenantId != null ? " WHERE tenant_id = @tenantId" : "";
        cmd.CommandText = $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {tenantFilter}
                            ORDER BY rowid DESC
                            LIMIT @lim";
        cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));
        if (tenantId != null)
        {
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
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
        var list = new List<AuditLogEntry>();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetTable))
        {
            whereClauses.Add("LOWER(target_table) = LOWER(@targetTable)");
            cmd.Parameters.AddWithValue("@targetTable", targetTable);
        }
        if (actorSid != null)
        {
            whereClauses.Add("actor_sid = @actorSid");
            cmd.Parameters.AddWithValue("@actorSid", actorSid.Value.Value);
        }
        if (since != null)
        {
            whereClauses.Add("occurred_at >= @since");
            cmd.Parameters.AddWithValue("@since", AuditTimestamp.ToStorage(since.Value));
        }
        if (tenantId != null)
        {
            whereClauses.Add("tenant_id = @tenantId");
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        cmd.CommandText = $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {whereSql}
                            ORDER BY rowid DESC
                            LIMIT @lim";
        cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
    }

    private static AuditLogEntry ReadAuditEntry(NpgsqlDataReader reader)
    {
        return new AuditLogEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(1)),
            EventType = reader.GetString(2),
            ActorSid = new Sid(reader.GetString(3)),
            TargetTable = reader.GetString(4),
            TargetColumn = reader.IsDBNull(5) ? null : reader.GetString(5),
            Decision = reader.GetString(6),
            TraceId = reader.GetString(7),
            DetailsJson = reader.GetString(8),
            PrevHash = reader.GetString(9),
            EntryHash = reader.GetString(10),
            TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant)
        };
    }

    public async Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        var size = query.EffectivePageSize;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "seq IS NOT NULL" };
        if (query.Tenant is { } tenant)
        {
            where.Add("tenant_id = @tenantId");
            cmd.Parameters.AddWithValue("@tenantId", tenant.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetTable))
        {
            where.Add("LOWER(target_table) = LOWER(@targetTable)");
            cmd.Parameters.AddWithValue("@targetTable", query.TargetTable);
        }

        if (query.Actor is { } actor && !string.IsNullOrWhiteSpace(actor.Value))
        {
            where.Add("actor_sid = @actorSid");
            cmd.Parameters.AddWithValue("@actorSid", actor.Value);
        }

        if (query.Since is { } since)
        {
            where.Add("occurred_at >= @since");
            cmd.Parameters.AddWithValue("@since", AuditTimestamp.ToStorage(since));
        }

        if (query.BeforeSequence is { } before)
        {
            where.Add("seq < @before");
            cmd.Parameters.AddWithValue("@before", before);
        }

        cmd.CommandText = $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                            FROM AUDIT_LOG_ENTRIES
                            WHERE {string.Join(" AND ", where)}
                            ORDER BY seq DESC
                            LIMIT @lim";
        cmd.Parameters.AddWithValue("@lim", size + 1);

        var items = new List<AuditLogEntry>(size + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadAuditEntry(reader);
            entry.ChainSequence = reader.GetInt64(12);
            items.Add(entry);
        }

        return AuditPageSlicer.Slice(items, size);
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        if (_auditChainViolation != null)
        {
            return false;
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, seq, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            ORDER BY rowid ASC";

        // Review PG-1: snapshot of what this process knows, taken under the writer lock so hash and seq belong together.
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

        string? hashAtKnownSeq = null;
        AuditPurgeCheckpoint? checkpoint = TryLoadVerifiedPurgeCheckpoint(out bool checkpointInvalid);
        if (checkpointInvalid)
        {
            FlagAuditChainViolation("External audit purge checkpoint has an invalid signature or format.");
            return false;
        }

        var expectedPrevHash = checkpoint != null ? checkpoint.SurvivingPrevHash : AuditGenesisHash;
        long position = checkpoint != null ? checkpoint.FirstSurvivingSequence - 1 : 0;
        AuditChainAnchor? anchor = TryLoadVerifiedAnchor(out bool anchorInvalid);
        if (anchorInvalid)
        {
            return false;
        }

        bool anchorEntryMatched = false;
        bool readAnyRows = false;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            readAnyRows = true;
            position++;
            var id = reader.GetString(0);
            var occurredAt = DateTimeOffset.Parse(reader.GetString(1));
            var eventType = reader.GetString(2);
            var actorSid = reader.GetString(3);
            var targetTable = reader.GetString(4);
            var targetColumn = reader.IsDBNull(5) ? null : reader.GetString(5);
            var decision = reader.GetString(6);
            var traceId = reader.GetString(7);
            var detailsJson = reader.GetString(8);
            var prevHash = reader.GetString(9);
            var entryHash = reader.GetString(10);
            long? sequence = reader.IsDBNull(11) ? null : reader.GetInt64(11);
            var tenantId = reader.IsDBNull(12) ? TenantId.LegacySingleTenant.Value : reader.GetString(12);

            if (checkpoint != null && sequence.HasValue && sequence.Value < checkpoint.FirstSurvivingSequence)
            {
                FlagAuditChainViolation($"Audit entry found before purge checkpoint sequence: got seq {sequence.Value}, checkpoint first sequence is {checkpoint.FirstSurvivingSequence}.");
                return false;
            }

            if (sequence.HasValue && sequence.Value != position)
            {
                FlagAuditChainViolation($"Audit entry sequence gap/divergence: expected seq {position}, got {sequence.Value}.");
                return false;
            }

            if (!FixedTimeEqualsString(prevHash, expectedPrevHash))
            {
                FlagAuditChainViolation($"Broken prev_hash chain at position {position} (id {id}). Expected {expectedPrevHash}, got {prevHash}.");
                return false;
            }

            var computedHash = ComputeAuditEntryHash(
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

            if (!FixedTimeEqualsString(entryHash, computedHash))
            {
                FlagAuditChainViolation($"Invalid entry_hash at position {position} (id {id}). Hash does not match row payload.");
                return false;
            }

            if (anchor != null && anchor.Sequence == position)
            {
                anchorEntryMatched = FixedTimeEqualsString(entryHash, anchor.EntryHash);
                if (!anchorEntryMatched)
                {
                    FlagAuditChainViolation($"Anchor mismatch at sequence {position}. DB entry hash does not match signed anchor.");
                    return false;
                }
            }

            if (position == knownSeq)
            {
                hashAtKnownSeq = entryHash;
            }

            expectedPrevHash = entryHash;
        }

        if (checkpoint != null && (!readAnyRows || position < checkpoint.FirstSurvivingSequence))
        {
            FlagAuditChainViolation($"Audit chain missing surviving rows: checkpoint expected seq {checkpoint.FirstSurvivingSequence} but DB has none.");
            return false;
        }

        // The anchor may lag behind the chain (other replicas, anchor written after commit); it must never be ahead of it.
        if (anchor != null && anchor.Sequence > 0 && (anchor.Sequence > position || !anchorEntryMatched))
        {
            FlagAuditChainViolation($"External signed anchor claims seq {anchor.Sequence}, but DB chain ended at position {position}.");
            return false;
        }

        // Review PG-1: a longer chain than this process knew is normal (other replicas / concurrent writers); a shorter one,
        // or a different hash at the known position, is a violation.
        if (knownSeq > 0 && knownSeq >= (checkpoint?.FirstSurvivingSequence ?? 1) && (position < knownSeq || !FixedTimeEqualsString(hashAtKnownSeq, knownHash)))
        {
            FlagAuditChainViolation($"Tail divergence: last known (seq {knownSeq}, hash {knownHash}) vs scanned chain (seq {position}, hash at known seq {hashAtKnownSeq ?? "n/a"}).");
            return false;
        }

        if (position > 0)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_pendingAuditTxStates)
                {
                    if (position > _lastAuditSeq)
                    {
                        _lastAuditSeq = position;
                        _lastAuditHash = expectedPrevHash;
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT MIN(rowid), MAX(rowid)
                            FROM AUDIT_LOG_ENTRIES
                            WHERE occurred_at >= @from AND occurred_at < @to";
        cmd.Parameters.AddWithValue("@from", AuditTimestamp.ToStorage(windowFrom));
        cmd.Parameters.AddWithValue("@to", AuditTimestamp.ToStorage(windowTo));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        var firstRowId = reader.GetInt64(0);
        var lastRowId = reader.GetInt64(1);
        await reader.CloseAsync().ConfigureAwait(false);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";
        countCmd.Parameters.AddWithValue("@a", firstRowId);
        countCmd.Parameters.AddWithValue("@b", lastRowId);
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return new AuditChainRange(firstRowId, lastRowId, count);
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);

        var list = new List<AuditChainRecord>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column,
                                   decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            WHERE rowid > @after AND rowid <= @last
                            ORDER BY rowid ASC
                            LIMIT @lim";
        cmd.Parameters.AddWithValue("@after", afterRowId);
        cmd.Parameters.AddWithValue("@last", lastRowIdInclusive);
        cmd.Parameters.AddWithValue("@lim", Math.Clamp(pageSize, 1, 10000));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long? seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            var entry = new AuditLogEntry
            {
                Id = Guid.Parse(reader.GetString(2)),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(3)),
                EventType = reader.GetString(4),
                ActorSid = new Sid(reader.GetString(5)),
                TargetTable = reader.GetString(6),
                TargetColumn = reader.IsDBNull(7) ? null : reader.GetString(7),
                Decision = reader.GetString(8),
                TraceId = reader.GetString(9),
                DetailsJson = reader.GetString(10),
                PrevHash = reader.GetString(11),
                EntryHash = reader.GetString(12),
                TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(13), out var tid) ? tid : TenantId.LegacySingleTenant)
            };
            list.Add(new AuditChainRecord(reader.GetInt64(0), seq, entry));
        }
        return list;
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    private (string? Hash, long Sequence) ReadAuditTail(NpgsqlTransaction? tx)
    {
        string? hash = null;
        long? seq = null;

        var conn = tx?.Connection ?? _dataSource.OpenConnection();
        bool ownsConn = tx == null;
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    hash = reader.GetString(0);
                    seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            if (hash == null) return (null, 0);
            if (seq.HasValue) return (hash, seq.Value);

            using var countCmd = conn.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";
            var count = Convert.ToInt64(countCmd.ExecuteScalar());
            return (hash, count);
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
        string? tenantId)
    {
        return AuditChainEngine.ComputeEntryHash(
            _auditHmacKey,
            sequence,
            id,
            prevHash,
            occurredAt.ToUniversalTime(),
            eventType,
            actorSid,
            targetTable,
            targetColumn,
            decision,
            traceId,
            detailsJson,
            tenantId);
    }

    private AuditChainAnchor CreateSignedAnchor(long sequence, string entryHash)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        return new AuditChainAnchor(sequence, entryHash, updatedAt, ComputeAnchorSignature(sequence, entryHash, updatedAt));
    }

    private string ComputeAnchorSignature(long sequence, string entryHash, DateTimeOffset updatedAt)
    {
        var data = Encoding.UTF8.GetBytes($"anchor-v1|{sequence}|{entryHash}|{updatedAt.ToUniversalTime():O}");
        return Convert.ToHexString(HMACSHA256.HashData(_auditAnchorKey, data));
    }

    private AuditChainAnchor? TryLoadVerifiedAnchor(out bool invalid)
    {
        invalid = false;
        AuditChainAnchor? anchor;
        try
        {
            anchor = _auditAnchorStore?.Load();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load audit anchor from external store.");
            invalid = true;
            return null;
        }

        if (anchor == null) return null;

        var expected = ComputeAnchorSignature(anchor.Sequence, anchor.EntryHash, anchor.UpdatedAt);
        if (!FixedTimeEqualsString(expected, anchor.Signature))
        {
            _logger?.LogCritical("External audit chain anchor signature INVALID at sequence {Sequence}.", anchor.Sequence);
            invalid = true;
            return null;
        }

        return anchor;
    }

    private AuditPurgeCheckpoint? TryLoadVerifiedPurgeCheckpoint(out bool invalid)
    {
        invalid = false;
        AuditPurgeCheckpoint? checkpoint;
        try
        {
            checkpoint = _auditAnchorStore?.LoadPurgeCheckpoint();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load audit purge checkpoint from external store.");
            invalid = true;
            return null;
        }

        if (checkpoint == null) return null;

        if (checkpoint.FirstSurvivingSequence < 1 || string.IsNullOrEmpty(checkpoint.SurvivingPrevHash) || string.IsNullOrEmpty(checkpoint.Signature) || checkpoint.PurgedCount < 0)
        {
            invalid = true;
            return null;
        }

        if (!AuditChainEngine.VerifyPurgeCheckpointSignature(_auditHmacKey, checkpoint))
        {
            _logger?.LogCritical("External audit purge checkpoint signature INVALID at sequence {Sequence}.", checkpoint.FirstSurvivingSequence);
            invalid = true;
            return null;
        }

        return checkpoint;
    }

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
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));
}
