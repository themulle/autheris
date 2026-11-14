using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class GovernanceRepositoryAuditShutdownTests
{
    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public async Task Cancel_during_batch_write_persists_or_dead_letters_every_entry(string provider)
    {
        var db = new FakeAuditStore();
        var insertStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        db.OnAuditInsertAsync = async (ct) =>
        {
            insertStarted.TrySetResult();
            await releaseInsert.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        };

        var logger = new RecordingLogger();
        await using var repo = CreateRepository(provider, db, logger);

        var e1 = CreateTierBEntry("cancel-write-1");
        var e2 = CreateTierBEntry("cancel-write-2");
        await repo.RecordAuditEventAsync(e1);
        await repo.RecordAuditEventAsync(e2);

        await insertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = repo.DisposeAsync().AsTask();
        releaseInsert.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        var totalAccounted = db.PersistedEntriesCount + db.DeadLetteredEntryCount;
        totalAccounted.ShouldBe(2);
        (await CreateRepository(provider, db, logger).VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_while_waiting_for_audit_lock_keeps_batch()
    {
        var db = new FakeAuditStore();
        var tierAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTierA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int insertCalls = 0;

        db.OnAuditInsertAsync = async (ct) =>
        {
            if (Interlocked.Increment(ref insertCalls) == 1)
            {
                tierAEntered.TrySetResult();
                await releaseTierA.Task.ConfigureAwait(false);
            }
        };

        var logger = new RecordingLogger();
        await using var repo = CreateRepository("PostgreSql", db, logger);

        // Hold _auditLock via a Tier-A synchronous write.
        var tierA = CreateTierAEntry("lock-holder");
        var tierATask = repo.RecordAuditEventAsync(tierA);
        await tierAEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Enqueue a Tier-B entry that the background processor dequeues and waits on _auditLock for.
        var tierB = CreateTierBEntry("waiting-for-lock");
        await repo.RecordAuditEventAsync(tierB);

        // Allow background processor thread to dequeue tierB and reach _auditLock.WaitAsync.
        await WaitHelper.WaitUntil(() => db.ChannelDequeueSettledCheck(), TimeSpan.FromSeconds(5));

        var disposeTask = repo.DisposeAsync().AsTask();
        releaseTierA.TrySetResult();

        await Task.WhenAll(tierATask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5));

        var totalAccounted = db.PersistedEntriesCount + db.DeadLetteredEntryCount;
        totalAccounted.ShouldBe(2);
        db.PersistedTraces.ShouldContain("waiting-for-lock");
    }

    [Fact]
    public async Task Dead_letter_failure_increments_lost_counter_and_logs_critical()
    {
        var db = new FakeAuditStore
        {
            FailAuditInsert = _ => new FakeSqlDbException("primary insert failed", "53300"),
            FailDeadLetterInsert = _ => new FakeSqlDbException("dead-letter table unavailable", "53300")
        };

        long lostDelta = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == GatewayDiagnostics.AuditLostOnShutdownCounter.Name)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, GatewayDiagnostics.AuditLostOnShutdownCounter))
            {
                Interlocked.Add(ref lostDelta, measurement);
            }
        });
        listener.Start();

        var logger = new RecordingLogger();
        await using var repo = CreateRepository("PostgreSql", db, logger);

        var entry = new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "TABLE_QUERY",
            ActorSid = new Sid("sensitive-actor-secret-999"),
            TargetTable = "patients",
            Decision = "ALLOW",
            TraceId = "lost-trace-sentinel",
            DetailsJson = "{\"ssn\":\"123-45-6789\"}"
        };

        await repo.RecordAuditEventAsync(entry);
        await repo.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        lostDelta.ShouldBeGreaterThanOrEqualTo(1);
        logger.Entries.ShouldContain(l =>
            l.Level == LogLevel.Critical &&
            l.Message.Contains('1') &&
            !l.Message.Contains("123-45-6789", StringComparison.Ordinal) &&
            !l.Message.Contains("sensitive-actor-secret-999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dead_letter_write_failure_does_not_fault_the_writer()
    {
        var db = new FakeAuditStore();
        var batch1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBatch1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstAttemptSeen = 0;

        db.OnAuditInsertAsync = async _ =>
        {
            if (Interlocked.Exchange(ref firstAttemptSeen, 1) == 0)
            {
                batch1Started.TrySetResult();
                await releaseBatch1.Task.ConfigureAwait(false);
            }
        };
        db.FailAuditInsert = rows => rows.Any(r => r.TraceId == "batch-1-lost")
            ? new FakeSqlDbException("db offline", "08006")
            : null;
        db.FailDeadLetterInsert = row => row.BatchJson.Contains("batch-1-lost", StringComparison.Ordinal)
            ? new FakeSqlDbException("dead-letter insert also down", "08006")
            : null;

        var logger = new RecordingLogger();
        await using var repo = CreateRepository("PostgreSql", db, logger);

        // Enqueue batch 1 and wait until processor enters PersistOrDeadLetterAsync for batch 1.
        await repo.RecordAuditEventAsync(CreateTierBEntry("batch-1-lost"));
        await batch1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Enqueue batch 2 into the channel while batch 1 is still in flight (pipeline not faulted yet).
        await repo.RecordAuditEventAsync(CreateTierBEntry("batch-2-recovers"));

        // Release batch 1 so all retries and the dead-letter write fail; writer task must not fault.
        releaseBatch1.TrySetResult();

        await WaitHelper.WaitUntil(() => db.PersistedTraces.Contains("batch-2-recovers"), TimeSpan.FromSeconds(5));

        await repo.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref db.DeadLetterAttempts).ShouldBeGreaterThanOrEqualTo(1);
        repo.IsAuditPipelineFaulted.ShouldBeFalse();
        logger.Entries.ShouldNotContain(l => l.Message.Contains("Error while draining audit channel during disposal.", StringComparison.Ordinal));
        db.PersistedTraces.ShouldContain("batch-2-recovers");
    }

    [Fact]
    public async Task Dead_letter_reason_has_no_exception_message()
    {
        const string secretDriverMessage = "duplicate key value violates unique constraint with secret row value PII_SSN_987_65_4321";
        var db = new FakeAuditStore
        {
            FailAuditInsert = _ => new FakeSqlDbException(secretDriverMessage, "23505")
        };

        var logger = new RecordingLogger();
        await using var repo = CreateRepository("PostgreSql", db, logger);

        var entry = new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "TABLE_QUERY",
            ActorSid = new Sid("user-1"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "dl-reason-test",
            DetailsJson = "{\"password\":\"super-secret-pwd\",\"api_key\":\"key-xyz\",\"actor\":\"alice\"}"
        };

        await repo.RecordAuditEventAsync(entry);
        await WaitHelper.WaitUntil(() => db.DeadLetters.Count == 1, TimeSpan.FromSeconds(5));

        var dl = db.DeadLetters.Single();
        dl.ErrorMessage.ShouldNotContain(secretDriverMessage);
        dl.ErrorMessage.ShouldNotContain("PII_SSN_987_65_4321");
        dl.ErrorMessage.ShouldContain(nameof(FakeSqlDbException));
        dl.ErrorMessage.ShouldContain("23505");

        dl.BatchJson.ShouldNotContain("super-secret-pwd");
        dl.BatchJson.ShouldNotContain("key-xyz");
        dl.BatchJson.ShouldContain("[REDACTED]");
        dl.BatchJson.ShouldContain("alice");

        await repo.DisposeAsync();
    }

    [Fact]
    public async Task Replayed_dead_letter_keeps_chain_valid()
    {
        var db = new FakeAuditStore();
        int failInserts = 0;
        db.FailAuditInsert = _ => Volatile.Read(ref failInserts) == 1
            ? new FakeSqlDbException("temporary outage", "08001")
            : null;

        var logger = new RecordingLogger();
        await using var repo = CreateRepository("PostgreSql", db, logger);

        await repo.RecordAuditEventAsync(CreateTierAEntry("chain-1"));

        Volatile.Write(ref failInserts, 1);
        await repo.RecordAuditEventAsync(CreateTierBEntry("dl-entry-2"));
        await WaitHelper.WaitUntil(() => db.DeadLetters.Count == 1, TimeSpan.FromSeconds(5));

        Volatile.Write(ref failInserts, 0);
        await repo.RecordAuditEventAsync(CreateTierAEntry("chain-3"));

        int replayedBatches = await repo.ReplayAuditDeadLettersAsync();
        replayedBatches.ShouldBe(1);

        db.DeadLetters.ShouldBeEmpty();
        db.PersistedTraces.ShouldContain("chain-1");
        db.PersistedTraces.ShouldContain("chain-3");
        db.PersistedTraces.ShouldContain("dl-entry-2");
        db.PersistedEntriesCount.ShouldBe(3);

        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    private static GovernanceRepository CreateRepository(string provider, FakeAuditStore db, ILogger? logger = null)
    {
        IGovernanceSqlDialect dialect = provider == "SqlServer"
            ? new SqlServerGovernanceDialect(() => new FakeAuditDbConnection(db, isSqlServer: true))
            : new PostgreSqlGovernanceDialect(() => new FakeAuditDbConnection(db, isSqlServer: false));

        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: logger,
            auditAnchorStore: new InMemoryAuditChainAnchorStore());
        repo.InitializeAudit();
        return repo;
    }

    private static AuditLogEntry CreateTierAEntry(string trace) => new()
    {
        TenantId = TestTenants.Default,
        EventType = "CONSENT_GRANTED",
        ActorSid = new Sid("admin"),
        TargetTable = "users",
        Decision = "ALLOW",
        TraceId = trace,
        DetailsJson = "{\"scope\":\"read\"}"
    };

    private static AuditLogEntry CreateTierBEntry(string trace) => new()
    {
        TenantId = TestTenants.Default,
        EventType = "TABLE_QUERY",
        ActorSid = new Sid("reader"),
        TargetTable = "users",
        Decision = "ALLOW",
        TraceId = trace,
        DetailsJson = "{\"rows\":1}"
    };

    private sealed class FakeSqlDbException(string message, string? sqlState) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        private readonly object _gate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record DeadLetterRow(string Id, string BatchJson, string ErrorMessage, string FailedAt, string TenantId);

    private sealed record PersistedAuditRow(
        long Seq,
        string Id,
        string OccurredAt,
        string? EventType,
        string? ActorSid,
        string? TargetTable,
        string? TargetColumn,
        string? Decision,
        string? TraceId,
        string? DetailsJson,
        string PrevHash,
        string EntryHash,
        string TenantId);

    private sealed class FakeAuditStore
    {
        private readonly object _gate = new();
        private readonly List<PersistedAuditRow> _auditRows = new();
        private readonly List<DeadLetterRow> _deadLetters = new();
        public int DeadLetterAttempts;

        public Func<CancellationToken, Task>? OnAuditInsertAsync { get; set; }
        public Func<IReadOnlyList<PersistedAuditRow>, Exception?>? FailAuditInsert { get; set; }
        public Func<DeadLetterRow, Exception?>? FailDeadLetterInsert { get; set; }

        public int PersistedEntriesCount { get { lock (_gate) return _auditRows.Count; } }
        public int DeadLetteredEntryCount
        {
            get
            {
                lock (_gate)
                {
                    int total = 0;
                    foreach (var dl in _deadLetters)
                    {
                        var entries = JsonSerializer.Deserialize<List<AuditLogEntry>>(dl.BatchJson);
                        total += entries?.Count ?? 0;
                    }
                    return total;
                }
            }
        }

        public IReadOnlyList<string> PersistedTraces { get { lock (_gate) return _auditRows.Select(r => r.TraceId ?? string.Empty).ToArray(); } }
        public IReadOnlyList<DeadLetterRow> DeadLetters { get { lock (_gate) return _deadLetters.ToArray(); } }

        public bool ChannelDequeueSettledCheck()
        {
            // Small yield helper for tests without sleeping: returning true after a short spin lets processor loop reach wait.
            Thread.Yield();
            return true;
        }

        public (string? Hash, long Seq) GetTail()
        {
            lock (_gate)
            {
                if (_auditRows.Count == 0) return (null, 0);
                var last = _auditRows[^1];
                return (last.EntryHash, last.Seq);
            }
        }

        public long GetCount() { lock (_gate) return _auditRows.Count; }

        public IReadOnlyList<PersistedAuditRow> GetVerifyRows() { lock (_gate) return _auditRows.ToArray(); }

        public IReadOnlyList<DeadLetterRow> GetDeadLetters(int limit)
        {
            lock (_gate) return _deadLetters.Take(limit).ToArray();
        }

        public void DeleteDeadLetter(string id)
        {
            lock (_gate) _deadLetters.RemoveAll(r => r.Id == id);
        }

        public void StageInsertRows(List<PersistedAuditRow> rows, FakeAuditDbTransaction? tx)
        {
            if (FailAuditInsert?.Invoke(rows) is { } ex) throw ex;
            if (tx != null)
            {
                tx.StagedRows.AddRange(rows);
            }
            else
            {
                lock (_gate) _auditRows.AddRange(rows);
            }
        }

        public void CommitRows(List<PersistedAuditRow> rows)
        {
            lock (_gate) _auditRows.AddRange(rows);
        }

        public void AddDeadLetter(DeadLetterRow row)
        {
            Interlocked.Increment(ref DeadLetterAttempts);
            if (FailDeadLetterInsert?.Invoke(row) is { } ex) throw ex;
            lock (_gate) _deadLetters.Add(row);
        }
    }

    private sealed class FakeAuditDbConnection(FakeAuditStore store, bool isSqlServer) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeAuditDbTransaction(this, store);
        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DbTransaction>(new FakeAuditDbTransaction(this, store));
        protected override DbCommand CreateDbCommand() => new FakeAuditDbCommand(this, store, isSqlServer);
    }

    private sealed class FakeAuditDbTransaction(DbConnection conn, FakeAuditStore store) : DbTransaction
    {
        public List<PersistedAuditRow> StagedRows { get; } = new();
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() => store.CommitRows(StagedRows);
        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.CommitRows(StagedRows);
            return Task.CompletedTask;
        }
        public override void Rollback() => StagedRows.Clear();
        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            StagedRows.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditDbCommand(DbConnection conn, FakeAuditStore store, bool isSqlServer) : DbCommand
    {
        private readonly FakeParamCollection _parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParam();

        public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (CommandText.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (CommandText.Equals("sp_getapplock", StringComparison.OrdinalIgnoreCase))
            {
                foreach (FakeParam p in _parameters)
                {
                    if (p.Direction == ParameterDirection.ReturnValue) p.Value = 0;
                }
                return 0;
            }

            if (CommandText.Contains("AUDIT_LOG_ENTRIES", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                if (store.OnAuditInsertAsync != null)
                {
                    await store.OnAuditInsertAsync(cancellationToken).ConfigureAwait(false);
                }

                var rows = ExtractInsertRows();
                store.StageInsertRows(rows, DbTransaction as FakeAuditDbTransaction);
                return rows.Count;
            }

            if (CommandText.Contains("AUDIT_DEAD_LETTER", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                var row = new DeadLetterRow(
                    GetString("@id"),
                    GetString("@batch"),
                    GetString("@err"),
                    GetString("@failed"),
                    GetString("@tenant"));
                store.AddDeadLetter(row);
                return 1;
            }

            if (CommandText.Contains("AUDIT_DEAD_LETTER", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                store.DeleteDeadLetter(GetString("@id"));
                return 1;
            }

            _ = isSqlServer;
            return 0;
        }

        public override object? ExecuteScalar()
        {
            if (CommandText.Contains("SESSIONPROPERTY('QUOTED_IDENTIFIER')", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase))
            {
                return store.GetCount();
            }
            return null;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteScalar());

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (CommandText.Contains("SELECT entry_hash, seq", StringComparison.OrdinalIgnoreCase) ||
                CommandText.Contains("TOP (1) entry_hash, seq", StringComparison.OrdinalIgnoreCase))
            {
                var (hash, seq) = store.GetTail();
                var rows = hash == null
                    ? Array.Empty<object?[]>()
                    : [new object?[] { hash, seq }];
                return Task.FromResult<DbDataReader>(new FakeAuditDbDataReader(["entry_hash", "seq"], rows));
            }

            if (CommandText.Contains("AUDIT_DEAD_LETTER", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                int limit = _parameters.Cast<FakeParam>().FirstOrDefault(p => p.ParameterName == "@lim")?.Value is int l ? l : 100;
                var rows = store.GetDeadLetters(limit)
                    .Select(d => new object?[] { d.Id, d.BatchJson })
                    .ToArray();
                return Task.FromResult<DbDataReader>(new FakeAuditDbDataReader(["id", "batch_json"], rows));
            }

            if (CommandText.Contains("FROM AUDIT_LOG_ENTRIES ORDER BY rowid ASC", StringComparison.OrdinalIgnoreCase))
            {
                var rows = store.GetVerifyRows()
                    .Select(r => new object?[]
                    {
                        r.Id, r.OccurredAt, r.EventType, r.ActorSid, r.TargetTable, r.TargetColumn,
                        r.Decision, r.TraceId, r.DetailsJson, r.PrevHash, r.EntryHash, r.Seq, r.TenantId
                    })
                    .ToArray();
                return Task.FromResult<DbDataReader>(new FakeAuditDbDataReader(
                    ["id", "occurred_at", "event_type", "actor_sid", "target_table", "target_column", "decision", "trace_id", "details_json", "prev_hash", "entry_hash", "seq", "tenant_id"],
                    rows));
            }

            return Task.FromResult<DbDataReader>(new FakeAuditDbDataReader([], []));
        }

        private List<PersistedAuditRow> ExtractInsertRows()
        {
            var dict = _parameters.Cast<FakeParam>().ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var list = new List<PersistedAuditRow>();
            for (int k = 0; dict.ContainsKey($"@id{k}"); k++)
            {
                list.Add(new PersistedAuditRow(
                    Convert.ToInt64(dict[$"@seq{k}"]),
                    Convert.ToString(dict[$"@id{k}"])!,
                    Convert.ToString(dict[$"@occ{k}"])!,
                    ToNullableString(dict[$"@event{k}"]),
                    ToNullableString(dict[$"@actor{k}"]),
                    ToNullableString(dict[$"@target{k}"]),
                    ToNullableString(dict[$"@col{k}"]),
                    ToNullableString(dict[$"@dec{k}"]),
                    ToNullableString(dict[$"@trace{k}"]),
                    ToNullableString(dict[$"@det{k}"]),
                    Convert.ToString(dict[$"@prev{k}"])!,
                    Convert.ToString(dict[$"@hash{k}"])!,
                    Convert.ToString(dict[$"@tenantId{k}"])!));
            }
            return list;
        }

        private string GetString(string name) =>
            Convert.ToString(_parameters.Cast<FakeParam>().First(p => p.ParameterName == name).Value) ?? string.Empty;

        private static string? ToNullableString(object? val) =>
            val == null || val == DBNull.Value ? null : Convert.ToString(val);
    }

    private sealed class FakeParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeAuditDbDataReader(string[] columns, object?[][] rows) : DbDataReader
    {
        private int _index = -1;
        public override int FieldCount => columns.Length;
        public override bool HasRows => rows.Length > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => rows[_index][ordinal]!;
        public override object this[string name] => rows[_index][GetOrdinal(name)]!;
        public override bool Read() => ++_index < rows.Length;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override int GetOrdinal(string name) => Array.FindIndex(columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        public override string GetName(int ordinal) => columns[ordinal];
        public override string GetDataTypeName(int ordinal) => "text";
        public override Type GetFieldType(int ordinal) => rows[_index][ordinal]?.GetType() ?? typeof(string);
        public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;
        public override int GetValues(object[] values) { Array.Copy(rows[_index], values, FieldCount); return FieldCount; }
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_index][ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_index][ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_index][ordinal]);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_index][ordinal]);
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_index][ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_index][ordinal]);
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(rows[_index][ordinal]);
        public override string GetString(int ordinal) => Convert.ToString(rows[_index][ordinal])!;
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_index][ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_index][ordinal]);
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_index][ordinal]);
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();
    }
}
