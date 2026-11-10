namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public partial class GovernanceRepository : IAuditLogRepository, IAuditChainExportSource, IAuditPurgeKeyProvider, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private volatile bool _isAuditPipelineFaulted;
    public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted || Volatile.Read(ref _auditChainViolation) != null;

    private Channel<AuditLogEntry> _auditChannel = null!;
    private readonly CancellationTokenSource _auditCts = new();
    private Task? _auditProcessorTask;
    private string _lastAuditHash = AuditSqlCore.AuditGenesisHash;
    private static readonly byte[] ProcessEphemeralAuditHmacKey = RandomNumberGenerator.GetBytes(32);
    private long _lastAuditSeq;
    private byte[] _auditHmacKey = null!;
    private byte[] _auditAnchorKey = null!;
    private IAuditChainAnchorStore? _auditAnchorStore;
    private bool _isDevOrTest;
    private string? _auditChainViolation;
    private ICallerContextProvider? _callerContextProvider;
    private int _auditFaultEventEmitted;
    private int _isDisposed;
    private readonly ConcurrentDictionary<DbTransaction, AuditTxPendingState> _pendingAuditTxStates = new();

    internal sealed class AuditTxPendingState
    {
        public required long LastSeq { get; set; }
        public required string LastHash { get; set; }
    }

    private void InitializeAuditFields(
        IOptions<GatewayOptions>? options,
        IAuditChainAnchorStore? auditAnchorStore,
        IKeyVaultSecretProvider? secretProvider,
        IHostEnvironment? environment,
        ICallerContextProvider? callerContextProvider)
    {
        _callerContextProvider = callerContextProvider;

        var channelOptions = new BoundedChannelOptions(Math.Max(1, options?.Value?.Audit?.QueryAuditChannelCapacity ?? 5000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _auditChannel = Channel.CreateBounded<AuditLogEntry>(channelOptions);

        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        _isDevOrTest = environment == null ||
                       string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

        byte[]? key = null;
        var auditSecretRef = options?.Value?.GovernanceDb?.AuditHmacKeyVaultRef;
        if (secretProvider != null && !string.IsNullOrWhiteSpace(auditSecretRef))
        {
            try
            {
                key = secretProvider.GetSecretBytes(auditSecretRef);
            }
            catch (Exception)
            {
                if (!_isDevOrTest)
                {
                    throw new InvalidOperationException("Security critical: Failed to load AuditHmacKeyVaultRef from Key Vault in non-development environment.");
                }
            }
        }

        if (key != null)
        {
            Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(key, "The audit HMAC key (AuditHmacKeyVaultRef)", _isDevOrTest);
        }

        if (key == null && secretProvider != null && !string.IsNullOrWhiteSpace(options?.Value?.DataMasking?.HmacSecretKeyVaultRef))
        {
            byte[]? masterKey = null;
            try
            {
                masterKey = secretProvider.GetSecretBytes(options.Value.DataMasking.HmacSecretKeyVaultRef);
            }
            catch (Exception)
            {
                if (!_isDevOrTest)
                {
                    throw new InvalidOperationException("Security critical: Failed to load DataMasking:HmacSecretKeyVaultRef from Key Vault in non-development environment.");
                }
            }

            if (masterKey != null && masterKey.Length > 0)
            {
                Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(masterKey, "The HMAC master key (HmacSecretKeyVaultRef)", _isDevOrTest);
                try
                {
                    key = HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
                        masterKey,
                        32,
                        info: "Autheris:AuditChain:v1"u8.ToArray());
                }
                catch
                {
                    // Fallback below
                }
            }
        }

        if (key == null || key.Length == 0)
        {
            if (!_isDevOrTest)
            {
                throw new InvalidOperationException(
                    "Security critical: No AuditHmacKeyVaultRef or HmacSecretKeyVaultRef configured in non-development environment. Cannot ensure audit log integrity.");
            }

            key = ProcessEphemeralAuditHmacKey;
        }

        _auditHmacKey = key;

        _auditAnchorKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _auditHmacKey,
            32,
            info: "Autheris:AuditAnchor:v1"u8.ToArray());

        if (!_isDevOrTest && auditAnchorStore is InMemoryAuditChainAnchorStore)
        {
            throw new InvalidOperationException(
                "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
        }

        _auditAnchorStore = auditAnchorStore;
    }

    private static bool IsInMemoryStore(IAuditChainAnchorStore? store) =>
        store is InMemoryAuditChainAnchorStore || (store is GovernanceAuditChainAnchorStore g && IsInMemoryStore(g.Inner)) || (store is SigningAuditChainAnchorStore s && IsInMemoryStore(s.Inner));

    internal static class AuditSql
    {
        public const string SelectLatestPurgeCheckpointPg = "SELECT first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature FROM AUDIT_PURGE_CHECKPOINTS ORDER BY first_surviving_seq DESC LIMIT 1";
        public const string SelectLatestPurgeCheckpointSs = "SELECT TOP (1) first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature FROM dbo.AUDIT_PURGE_CHECKPOINTS ORDER BY first_surviving_seq DESC";
        public const string InsertPurgeCheckpointPg = "INSERT INTO AUDIT_PURGE_CHECKPOINTS (id, first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature) VALUES (@id, @firstSeq, @prevHash, @purgedCount, @purgedThrough, @createdAt, @sig, @extSig)";
        public const string InsertPurgeCheckpointSs = "INSERT INTO dbo.AUDIT_PURGE_CHECKPOINTS (id, first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature) VALUES (@id, @firstSeq, @prevHash, @purgedCount, @purgedThrough, @createdAt, @sig, @extSig)";
        public static readonly AuditPurgePlanStatements PurgePlanPg = new(
            SelectOldestRowSql: "SELECT seq, occurred_at FROM AUDIT_LOG_ENTRIES ORDER BY seq ASC LIMIT 1",
            SelectSurvivingRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES WHERE occurred_at >= @threshold ORDER BY seq ASC LIMIT 1",
            SelectNewestRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq DESC LIMIT 1",
            SelectMaxEligibleSeqSql: "SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES WHERE seq < @survivingSeq");
        public static readonly AuditPurgePlanStatements PurgePlanSs = new(
            SelectOldestRowSql: "SELECT TOP 1 seq, occurred_at FROM dbo.AUDIT_LOG_ENTRIES ORDER BY seq ASC",
            SelectSurvivingRowSql: "SELECT TOP 1 seq, prev_hash FROM dbo.AUDIT_LOG_ENTRIES WHERE occurred_at >= @threshold ORDER BY seq ASC",
            SelectNewestRowSql: "SELECT TOP 1 seq, prev_hash FROM dbo.AUDIT_LOG_ENTRIES ORDER BY seq DESC",
            SelectMaxEligibleSeqSql: "SELECT MAX(seq) FROM dbo.AUDIT_LOG_ENTRIES WHERE seq < @survivingSeq");
        public static string GetAuditLogEntriesPg(bool withTenant) => string.Concat("SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES ", withTenant ? "WHERE tenant_id = @tenantId " : string.Empty, "ORDER BY rowid DESC LIMIT @lim");
        public static string GetAuditLogEntriesSs(bool withTenant) => string.Concat("SELECT TOP (@lim) id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES ", withTenant ? "WHERE tenant_id = @tenantId " : string.Empty, "ORDER BY rowid DESC");
        public static string FormatQueryAuditLogsPg(string whereSql) => string.Concat("SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES ", whereSql, " ORDER BY rowid DESC LIMIT @lim");
        public static string FormatQueryAuditLogsSs(string whereSql) => string.Concat("SELECT TOP (@lim) id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES ", whereSql, " ORDER BY rowid DESC");
        public static string FormatQueryAuditLogPagePg(string wherePredicate) => string.Concat("SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES WHERE ", wherePredicate, " ORDER BY seq DESC LIMIT @lim");
        public static string FormatQueryAuditLogPageSs(string wherePredicate) => string.Concat("SELECT TOP (@lim) id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq FROM AUDIT_LOG_ENTRIES WHERE ", wherePredicate, " ORDER BY seq DESC");
        public const string SelectChainRangeMinMax = "SELECT MIN(rowid), MAX(rowid) FROM AUDIT_LOG_ENTRIES WHERE occurred_at >= @from AND occurred_at < @to";
        public const string SelectChainRangeCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";
        public const string SelectChainPagePg = "SELECT rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id FROM AUDIT_LOG_ENTRIES WHERE rowid > @after AND rowid <= @last ORDER BY rowid ASC LIMIT @lim";
        public const string SelectChainPageSs = "SELECT TOP (@lim) rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id FROM AUDIT_LOG_ENTRIES WHERE rowid > @after AND rowid <= @last ORDER BY rowid ASC";
        public const string SelectTailPg = "SELECT entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";
        public const string SelectTailSs = "SELECT TOP (1) entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC";
        public const string SelectCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";
        public const string SelectVerifyScan = "SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, seq, tenant_id FROM AUDIT_LOG_ENTRIES ORDER BY rowid ASC";
    }

    internal void InitializeAudit()
    {
        InitializeAuditChainAnchor();
        _auditProcessorTask ??= Task.Run(ProcessAuditChannelAsync, CancellationToken.None);
    }

    private void InitializeAuditChainAnchor()
    {
        if (_auditAnchorStore == null) return;
        if (!_isDevOrTest && _auditAnchorStore is InMemoryAuditChainAnchorStore)
        {
            AuditSqlCore.LogInMemoryAnchorWarning(_logger);
        }

        var anchor = TryLoadVerifiedAnchor(out bool invalid);
        if (invalid)
        {
            FlagAuditChainViolation("External signed audit anchor signature verification failed at startup.");
            return;
        }

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
                    AuditSqlCore.LogSeedAnchorError(ex, _lastAuditSeq, _logger);
                }
            }
            return;
        }

        if (_lastAuditSeq < anchor.Sequence)
        {
            FlagAuditChainViolation(
                $"Audit chain truncated! External verified anchor has sequence {anchor.Sequence}, but database only reaches {_lastAuditSeq}.");
        }
        else if (_lastAuditSeq == anchor.Sequence && !AuditSqlCore.FixedTimeEqualsString(_lastAuditHash, anchor.EntryHash))
        {
            FlagAuditChainViolation(
                $"Audit chain tail mutated! Sequence {anchor.Sequence} matches external anchor, but hash '{_lastAuditHash}' does not match anchored '{anchor.EntryHash}'.");
        }
    }

    public async Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = IsPostgreSql ? AuditSql.SelectLatestPurgeCheckpointPg : AuditSql.SelectLatestPurgeCheckpointSs;
        return await AuditSqlCore.GetLatestPurgeCheckpointAsync(conn, sql, ct).ConfigureAwait(false);
    }

    public async Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        _auditAnchorStore?.SavePurgeCheckpoint(checkpoint);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = IsPostgreSql ? AuditSql.InsertPurgeCheckpointPg : AuditSql.InsertPurgeCheckpointSs;
        await AuditSqlCore.SavePurgeCheckpointAsync(conn, sql, checkpoint, ct).ConfigureAwait(false);
    }

    public async Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(
        DateTimeOffset thresholdUtc,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var plan = IsPostgreSql ? AuditSql.PurgePlanPg : AuditSql.PurgePlanSs;
        return await AuditSqlCore.PlanPurgePrefixAsync(conn, plan, thresholdUtc, ct).ConfigureAwait(false);
    }

    public async Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.ExecutePurgeBatchAsync(conn, _dialect, maxSequenceInclusive, batchSize, ct).ConfigureAwait(false);
    }

    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        StoredTenant.RequireWritable(entry.TenantId);

        if (entry.Caller == null && _callerContextProvider != null)
        {
            entry.Caller = _callerContextProvider.ResolveCallerContext();
        }

        bool isTierA = AuditChainViolationGate.IsTierA(entry);
        var violation = Volatile.Read(ref _auditChainViolation);
        var admitted = AuditChainViolationGate.Admit(entry, isTierA, violation, ViolationPolicy, _logger);

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
            AuditSqlCore.LogPipelineFaulted(entry.TargetTable, _logger);
            throw new AuditUnavailableException("Audit pipeline is in a faulted state (fail-closed).");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _auditChannel.Writer.WriteAsync(entry, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AuditSqlCore.LogChannelSaturated(entry.TargetTable, _logger);
            throw new AuditUnavailableException("Audit pipeline saturated (fail-closed).");
        }
    }

    public async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        foreach (var candidate in entries ?? throw new ArgumentNullException(nameof(entries)))
        {
            StoredTenant.RequireWritable(candidate.TenantId);
        }

        if (entries.Count == 0)
        {
            return;
        }

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
            AuditSqlCore.LogFaultEventError(ex, _logger);
        }
    }

    public async Task FlushAuditChannelAsync(CancellationToken ct = default)
    {
        var batch = new List<AuditLogEntry>();
        async Task FlushBatchAsync()
        {
            if (batch.Count == 0) return;
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try { await RecordAuditEventsBatchInternalAsync(batch, ct).ConfigureAwait(false); }
            finally { _auditLock.Release(); }
            batch.Clear();
        }

        while (_auditChannel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250) await FlushBatchAsync().ConfigureAwait(false);
        }
        await FlushBatchAsync().ConfigureAwait(false);
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
                var deadline = TimeProvider.System.GetUtcNow().AddMilliseconds(10);

                while (batch.Count < 250 && (TimeProvider.System.GetUtcNow() < deadline || batch.Count == 0))
                {
                    if (reader.TryRead(out var entry)) batch.Add(entry);
                    else break;
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
                                AuditSqlCore.LogPipelineRecovered(_logger);
                            }
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            AuditSqlCore.LogBatchFlushError(ex, batch.Count, attempt, maxRetries, _logger);
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(50 * attempt, _auditCts.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _isAuditPipelineFaulted = true;
                                AuditSqlCore.LogFatalBatchFailure(ex, batch.Count, _logger);
                                await using var conn = await OpenConnectionAsync(_auditCts.Token).ConfigureAwait(false);
                                await AuditSqlCore.WriteToDeadLetterAsync(conn, _dialect, batch, ex.Message, _logger, _auditCts.Token).ConfigureAwait(false);
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
        }

        batch.Clear();
        async Task DrainBatchAsync()
        {
            if (batch.Count == 0) return;
            await _auditLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await RecordAuditEventsBatchInternalAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AuditSqlCore.LogShutdownDrainError(ex, batch.Count, _logger);
                await using var conn = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                await AuditSqlCore.WriteToDeadLetterAsync(conn, _dialect, batch, ex.Message, _logger, CancellationToken.None).ConfigureAwait(false);
                Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
            }
            finally
            {
                _auditLock.Release();
            }
            batch.Clear();
        }

        while (reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250) await DrainBatchAsync().ConfigureAwait(false);
        }
        await DrainBatchAsync().ConfigureAwait(false);
    }

    public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction? existingTx, CancellationToken ct = default)
    {
        if (_auditRecorder != null)
        {
            return _auditRecorder(entry, existingTx, ct);
        }
        if (existingTx == null)
        {
            return RecordAuditEventAsync(entry, ct);
        }
        return RecordAuditEventInternalAsync(entry, ct, existingTx);
    }

    internal Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct, DbTransaction? existingTx = null) =>
        RecordAuditEventsBatchInternalAsync([entry], ct, existingTx);

    public void OnTransactionCommitted(DbTransaction tx)
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
                    var anchor = CreateSignedAnchor(state.LastSeq, state.LastHash);
                    var current = TryLoadVerifiedAnchor(out var anchorInvalid);
                    if (anchorInvalid)
                    {
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
                    AuditSqlCore.LogAnchorPersistError(ex, state.LastSeq, _logger);
                    anchorFailure = ex;
                }
            }
        }

        if (anchorFailure != null)
        {
            throw new AuditUnavailableException("The audit chain anchor could not be persisted; the change is not acknowledged (fail-closed).", anchorFailure);
        }
    }

    public void OnTransactionRolledBack(DbTransaction tx)
    {
        _pendingAuditTxStates.TryRemove(tx, out _);
    }

    public void RollbackPendingAuditTransactions()
    {
        _pendingAuditTxStates.Clear();
    }

    private const int AuditWriteMaxAttempts = 5;

    private async Task RecordAuditEventsBatchInternalAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct, DbTransaction? existingTx = null)
    {
        if (batch.Count == 0) return;

        if (existingTx != null)
        {
            await WriteAuditBatchEnrolledAsync(batch, existingTx, ct).ConfigureAwait(false);
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAuditBatchOnceAsync(batch, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < AuditWriteMaxAttempts && AuditSqlCore.IsTransientWriteConflict(ex, _dialect))
            {
                AuditSqlCore.LogAuditWriteConflict(ex, attempt, AuditWriteMaxAttempts, _logger);
                await Task.Delay(Random.Shared.Next(10, 40) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAuditBatchEnrolledAsync(IReadOnlyList<AuditLogEntry> batch, DbTransaction existingTx, CancellationToken ct)
    {
        var conn = existingTx.Connection!;

        if (!_pendingAuditTxStates.TryGetValue(existingTx, out var pendingState))
        {
            await AuditSqlCore.AcquireAuditChainLockAsync(conn, existingTx, _dialect, ct).ConfigureAwait(false);

            var (dbTailHash, dbTailSeq) = ReadAuditTail(existingTx);
            pendingState = _pendingAuditTxStates.GetOrAdd(existingTx, _ => new AuditTxPendingState
            {
                LastSeq = dbTailSeq,
                LastHash = dbTailHash ?? AuditSqlCore.AuditGenesisHash
            });
        }

        long lastSequence = pendingState.LastSeq;
        string lastEntryHash = pendingState.LastHash;

        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;
            var effectiveTenantId = StoredTenant.RequireWritable(entry.TenantId);
            var entryHash = ComputeEntryHash(entry, currentSequence, currentPrevHash, effectiveTenantId);
            entry.PrevHash = currentPrevHash;
            entry.EntryHash = entryHash;
            rows.Add(ToInsertRow(entry, currentPrevHash, entryHash, effectiveTenantId, currentSequence));
            lastEntryHash = entryHash;
        }

        await AuditSqlCore.InsertAuditRowsAsync(conn, existingTx, _dialect, rows, ct).ConfigureAwait(false);

        pendingState.LastSeq = lastSequence;
        pendingState.LastHash = lastEntryHash;
    }

    private async Task WriteAuditBatchOnceAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct)
    {
        var violationAtStart = Volatile.Read(ref _auditChainViolation);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await AuditSqlCore.AcquireAuditChainLockAsync(conn, tx, _dialect, ct).ConfigureAwait(false);

        var (dbTailHash, dbTailSeq) = ReadAuditTail(tx);
        var effectiveDbHash = dbTailHash ?? AuditSqlCore.AuditGenesisHash;
        long knownSeq;
        string knownHash;
        lock (_pendingAuditTxStates)
        {
            knownSeq = _lastAuditSeq;
            knownHash = _lastAuditHash;
        }
        if (dbTailSeq < knownSeq || (dbTailSeq == knownSeq && !AuditSqlCore.FixedTimeEqualsString(effectiveDbHash, knownHash)))
        {
            FlagAuditChainViolation(
                $"Audit chain tail in DB (seq {dbTailSeq}) is behind or diverges with the last known tail (seq {knownSeq}) - possible truncation or rewrite.");
        }

        long lastSequence = dbTailSeq;
        string lastEntryHash = effectiveDbHash;

        var rows = new List<AuditInsertRow>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;
            var effectiveTenantId = StoredTenant.RequireWritable(entry.TenantId);
            var entryHash = ComputeEntryHash(entry, currentSequence, currentPrevHash, effectiveTenantId);
            entry.PrevHash = currentPrevHash;
            entry.EntryHash = entryHash;
            rows.Add(ToInsertRow(entry, currentPrevHash, entryHash, effectiveTenantId, currentSequence));
            lastEntryHash = entryHash;
        }

        await AuditSqlCore.InsertAuditRowsAsync(conn, tx, _dialect, rows, ct).ConfigureAwait(false);

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
                var current = TryLoadVerifiedAnchor(out var anchorInvalid);
                if (anchorInvalid)
                {
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
                AuditSqlCore.LogAnchorPersistError(ex, anchor.Sequence, _logger);
                anchorFailure = ex;
            }
        }

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
        var sql = IsPostgreSql ? AuditSql.GetAuditLogEntriesPg(tenantId != null) : AuditSql.GetAuditLogEntriesSs(tenantId != null);
        return await AuditSqlCore.GetAuditLogEntriesAsync(conn, sql, limit, tenantId, ct).ConfigureAwait(false);
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
        Func<string, string> formatter = IsPostgreSql ? AuditSql.FormatQueryAuditLogsPg : AuditSql.FormatQueryAuditLogsSs;
        return await AuditSqlCore.QueryAuditLogsAsync(conn, formatter, targetTable, actorSid, since, limit, tenantId, ct).ConfigureAwait(false);
    }

    public async Task<AuditLogPage> QueryAuditLogPageAsync(
        AuditLogPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        Func<string, string> formatter = IsPostgreSql ? AuditSql.FormatQueryAuditLogPagePg : AuditSql.FormatQueryAuditLogPageSs;
        return await AuditSqlCore.QueryAuditLogPageAsync(conn, formatter, query, ct).ConfigureAwait(false);
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
        var sql = IsPostgreSql ? AuditSql.SelectChainPagePg : AuditSql.SelectChainPageSs;
        return await AuditSqlCore.GetAuditChainPageAsync(conn, sql, afterRowId, lastRowIdInclusive, pageSize, ct).ConfigureAwait(false);
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    byte[]? IAuditPurgeKeyProvider.GetAuditHmacKey() => _auditHmacKey;

    private (string? Hash, long Sequence) ReadAuditTail(DbTransaction? tx)
    {
        var conn = tx?.Connection ?? _dialect.CreateConnection();
        bool ownsConn = tx == null;
        if (ownsConn && conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
        }
        try
        {
            var tailSql = IsPostgreSql ? AuditSql.SelectTailPg : AuditSql.SelectTailSs;
            return AuditSqlCore.ReadAuditTail(conn, tx, tailSql, AuditSql.SelectCount);
        }
        finally
        {
            if (ownsConn)
            {
                conn.Dispose();
            }
        }
    }

    private string ComputeAuditEntryHash(long? sequence, string id, string prevHash, DateTimeOffset occurredAt, string? eventType, string? actorSid, string? targetTable, string? targetColumn, string? decision, string? traceId, string? detailsJson, string? tenantId) =>
        AuditSqlCore.ComputeAuditEntryHash(_auditHmacKey, sequence, id, prevHash, occurredAt, eventType, actorSid, targetTable, targetColumn, decision, traceId, detailsJson, tenantId);

    private string ComputeEntryHash(AuditLogEntry entry, long seq, string prevHash, string tenantId) =>
        ComputeAuditEntryHash(seq, entry.Id.ToString(), prevHash, entry.OccurredAt, entry.EventType, entry.ActorSid.Value, entry.TargetTable, entry.TargetColumn, entry.Decision, entry.TraceId, entry.DetailsJson, tenantId);

    private static AuditInsertRow ToInsertRow(AuditLogEntry entry, string prevHash, string hash, string tenantId, long seq) =>
        new(entry.Id.ToString(), AuditTimestamp.ToStorage(entry.OccurredAt), entry.EventType, entry.ActorSid.Value, entry.TargetTable, entry.TargetColumn, entry.Decision, entry.TraceId, entry.DetailsJson, prevHash, hash, tenantId, seq);

    private AuditChainAnchor CreateSignedAnchor(long sequence, string entryHash) =>
        AuditSqlCore.CreateSignedAnchor(_auditAnchorKey, sequence, entryHash);

    private AuditChainAnchor? TryLoadVerifiedAnchor(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedAnchor(_auditAnchorStore, _auditAnchorKey, _logger, out invalid);

    private AuditPurgeCheckpoint? TryLoadVerifiedPurgeCheckpoint(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedPurgeCheckpoint(_auditAnchorStore, _auditHmacKey, _logger, out invalid);

    private void FlagAuditChainViolation(string reason)
    {
        if (Interlocked.CompareExchange(ref _auditChainViolation, reason, null) == null)
        {
            AuditSqlCore.LogChainViolation(reason, _logger);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;

        try
        {
            _auditCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            if (_auditProcessorTask != null)
            {
                await _auditProcessorTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AuditSqlCore.LogDisposalDrainError(ex, _logger);
        }

        try
        {
            _auditCts.Dispose();
        }
        catch (ObjectDisposedException) { }

        _auditLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
