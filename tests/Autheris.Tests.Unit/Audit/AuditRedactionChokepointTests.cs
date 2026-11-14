using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class AuditRedactionChokepointTests
{
    [Fact]
    public async Task Raw_DetailsJson_from_endpoint_is_redacted_before_persist()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        var directEntry = new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "raw-direct",
            DetailsJson = "{\"client_secret\":\"top-secret-42\",\"user\":\"alice\"}"
        };

        await repo.RecordAuditEventAsync(directEntry);

        var sinkRepo = new CaptureBatchAuditRepository();
        await using var sink = new AuditGroupCommitSink(sinkRepo, new AuditGroupCommitOptions { MaxBatchSize = 1, MaxBatchDelayMs = 0 });
        await sink.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "raw-sink",
            DetailsJson = "{\"x-api-key\":\"sink-secret-99\",\"user\":\"bob\"}"
        });

        var storedDirect = db.PersistedRows.Single(r => r.TraceId == "raw-direct");
        storedDirect.DetailsJson.ShouldNotBeNull();
        storedDirect.DetailsJson!.ShouldNotContain("top-secret-42");
        storedDirect.DetailsJson.ShouldContain("[REDACTED]");
        storedDirect.DetailsJson.ShouldContain("alice");

        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        var storedSink = sinkRepo.Entries.Single(e => e.TraceId == "raw-sink");
        storedSink.DetailsJson.ShouldNotContain("sink-secret-99");
        storedSink.DetailsJson.ShouldContain("[REDACTED]");
        storedSink.DetailsJson.ShouldContain("bob");
    }

    [Fact]
    public async Task Unparseable_details_never_stored_raw()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "bad-json",
            DetailsJson = "not-json password=raw-secret-999"
        });

        string oversized = JsonSerializer.Serialize(new { note = new string('X', AuditDetailsBuilder.MaxDetailsBytes + 1024), secret = "OVERSIZE_SECRET_XYZ" });
        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "oversize-json",
            DetailsJson = oversized
        });

        var badRow = db.PersistedRows.Single(r => r.TraceId == "bad-json");
        badRow.DetailsJson.ShouldBe(AuditDetailsBuilder.UnparseableJsonMarker);
        badRow.DetailsJson!.ShouldNotContain("raw-secret-999");

        var bigRow = db.PersistedRows.Single(r => r.TraceId == "oversize-json");
        bigRow.DetailsJson.ShouldBe(AuditDetailsBuilder.TruncatedJsonMarker);
        bigRow.DetailsJson!.ShouldNotContain("OVERSIZE_SECRET_XYZ");

        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Connection_string_in_value_is_scrubbed()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "scrub-values",
            DetailsJson = JsonSerializer.Serialize(new
            {
                keyset = "Host=db;Password=ConnSecret9;pwd=PwdSecret8;Database=app",
                error = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sigXYZ123",
                message = "postgres://svc:UrlPass7@pg.internal:5432/gov",
                endpoint = "https://api.example.com/v1"
            })
        });

        var row = db.PersistedRows.Single();
        row.DetailsJson.ShouldNotBeNull();
        row.DetailsJson!.ShouldNotContain("ConnSecret9");
        row.DetailsJson.ShouldNotContain("PwdSecret8");
        row.DetailsJson.ShouldNotContain("sigXYZ123");
        row.DetailsJson.ShouldNotContain("UrlPass7");
        row.DetailsJson.ShouldContain("[REDACTED]");
        row.DetailsJson.ShouldContain("https://api.example.com/v1");
    }

    [Fact]
    public async Task Collection_over_cap_is_truncated()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        var items = Enumerable.Range(0, 100).Select(i => i == 99 ? "item-99-secret" : $"item-{i}").ToArray();
        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "cap-array",
            DetailsJson = JsonSerializer.Serialize(new { items })
        });

        var row = db.PersistedRows.Single(r => r.TraceId == "cap-array");
        row.DetailsJson.ShouldNotBeNull();
        row.DetailsJson!.ShouldNotContain("item-99-secret");
        using var doc = JsonDocument.Parse(row.DetailsJson!);
        var arr = doc.RootElement.GetProperty("items");
        arr.GetArrayLength().ShouldBeLessThanOrEqualTo(AuditDetailsBuilder.MaxCollectionElements);
        arr.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == "[TRUNCATED]").ShouldBeTrue();
    }

    [Fact]
    public async Task Property_name_control_chars_replaced()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        string maliciousKey = "bad\r\nkey\u0085name\u2028x\u2029y";
        string maliciousVal = "val\u0007\u009f\u2028\u2029end";
        string json = JsonSerializer.Serialize(new Dictionary<string, string> { [maliciousKey] = maliciousVal });

        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "ctrl-chars",
            DetailsJson = json
        });

        var row = db.PersistedRows.Single();
        using var doc = JsonDocument.Parse(row.DetailsJson!);
        var prop = doc.RootElement.EnumerateObject().Single();
        foreach (char c in prop.Name + prop.Value.GetString())
        {
            char.IsControl(c).ShouldBeFalse();
            c.ShouldNotBe('\u2028');
            c.ShouldNotBe('\u2029');
        }
    }

    [Fact]
    public async Task Allow_list_is_exact_match()
    {
        var db = new FakeAuditStore();
        await using var repo = CreateRepository(db);

        await repo.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = TestTenants.Default,
            EventType = "CONSENT_GRANTED",
            ActorSid = new Sid("admin"),
            TargetTable = "orders",
            Decision = "ALLOW",
            TraceId = "allow-list",
            DetailsJson = JsonSerializer.Serialize(new
            {
                keyset = "page-1",
                primarykey = "id",
                key_columns = new[] { "tenant_id", "id" },
                idempotency_key_hash = "sha256:abc",
                idempotency_key = "raw-idem-secret",
                keyset_extra = "extra-key-secret"
            })
        });

        var row = db.PersistedRows.Single();
        using var doc = JsonDocument.Parse(row.DetailsJson!);
        var root = doc.RootElement;
        root.GetProperty("keyset").GetString().ShouldBe("page-1");
        root.GetProperty("primarykey").GetString().ShouldBe("id");
        root.GetProperty("key_columns")[0].GetString().ShouldBe("tenant_id");
        root.GetProperty("idempotency_key_hash").GetString().ShouldBe("sha256:abc");
        root.GetProperty("idempotency_key").GetString().ShouldBe("[REDACTED]");
        root.GetProperty("keyset_extra").GetString().ShouldBe("[REDACTED]");
        row.DetailsJson.ShouldNotBeNull();
        row.DetailsJson!.ShouldNotContain("raw-idem-secret");
        row.DetailsJson!.ShouldNotContain("extra-key-secret");
    }

    private static GovernanceRepository CreateRepository(FakeAuditStore db)
    {
        var dialect = new PostgreSqlGovernanceDialect(() => new FakeDbConnection(db));
        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore());
        repo.InitializeAudit();
        return repo;
    }

    private sealed class CaptureBatchAuditRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = new();
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordAuditEventsAsync([entry], ct);
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
        {
            Entries.AddRange(entries);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed record PersistedAuditRow(
        long Seq, string Id, string OccurredAt, string? EventType, string? ActorSid, string? TargetTable,
        string? TargetColumn, string? Decision, string? TraceId, string? DetailsJson, string PrevHash, string EntryHash, string TenantId);

    private sealed class FakeAuditStore
    {
        private readonly object _gate = new();
        public List<PersistedAuditRow> PersistedRows { get; } = new();

        public (string? Hash, long Seq) GetTail()
        {
            lock (_gate) return PersistedRows.Count == 0 ? (null, 0) : (PersistedRows[^1].EntryHash, PersistedRows[^1].Seq);
        }

        public long GetCount() { lock (_gate) return PersistedRows.Count; }
        public IReadOnlyList<PersistedAuditRow> Snapshot() { lock (_gate) return PersistedRows.ToArray(); }
        public void Commit(List<PersistedAuditRow> rows) { lock (_gate) PersistedRows.AddRange(rows); }
    }

    private sealed class FakeDbConnection(FakeAuditStore store) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this, store);
        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DbTransaction>(new FakeDbTransaction(this, store));
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this, store);
    }

    private sealed class FakeDbTransaction(DbConnection conn, FakeAuditStore store) : DbTransaction
    {
        public List<PersistedAuditRow> Staged { get; } = new();
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() => store.Commit(Staged);
        public override Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
        public override void Rollback() => Staged.Clear();
    }

    private sealed class FakeDbCommand(DbConnection conn, FakeAuditStore store) : DbCommand
    {
        private readonly FakeParams _params = new();
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParam();

        public override int ExecuteNonQuery()
        {
            if (CommandText.Contains("AUDIT_LOG_ENTRIES", StringComparison.OrdinalIgnoreCase) &&
                CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                var dict = _params.Cast<FakeParam>().ToDictionary(p => p.ParameterName, p => p.Value, StringComparer.OrdinalIgnoreCase);
                var rows = new List<PersistedAuditRow>();
                for (int k = 0; dict.ContainsKey($"@id{k}"); k++)
                {
                    rows.Add(new PersistedAuditRow(
                        Convert.ToInt64(dict[$"@seq{k}"]), Convert.ToString(dict[$"@id{k}"])!, Convert.ToString(dict[$"@occ{k}"])!,
                        Str(dict[$"@event{k}"]), Str(dict[$"@actor{k}"]), Str(dict[$"@target{k}"]), Str(dict[$"@col{k}"]),
                        Str(dict[$"@dec{k}"]), Str(dict[$"@trace{k}"]), Str(dict[$"@det{k}"]),
                        Convert.ToString(dict[$"@prev{k}"])!, Convert.ToString(dict[$"@hash{k}"])!, Convert.ToString(dict[$"@tenantId{k}"])!));
                }
                if (DbTransaction is FakeDbTransaction tx) tx.Staged.AddRange(rows);
                else store.Commit(rows);
                return rows.Count;
            }
            return 0;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(ExecuteNonQuery());
        public override object? ExecuteScalar() => CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase) ? store.GetCount() : null;
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SELECT entry_hash, seq", StringComparison.OrdinalIgnoreCase))
            {
                var (hash, seq) = store.GetTail();
                return new FakeReader(["entry_hash", "seq"], hash == null ? [] : [new object?[] { hash, seq }]);
            }
            if (CommandText.Contains("FROM AUDIT_LOG_ENTRIES ORDER BY rowid ASC", StringComparison.OrdinalIgnoreCase))
            {
                var rows = store.Snapshot().Select(r => new object?[]
                {
                    r.Id, r.OccurredAt, r.EventType, r.ActorSid, r.TargetTable, r.TargetColumn,
                    r.Decision, r.TraceId, r.DetailsJson, r.PrevHash, r.EntryHash, r.Seq, r.TenantId
                }).ToArray();
                return new FakeReader(
                    ["id", "occurred_at", "event_type", "actor_sid", "target_table", "target_column", "decision", "trace_id", "details_json", "prev_hash", "entry_hash", "seq", "tenant_id"],
                    rows);
            }
            return new FakeReader([], []);
        }
        private static string? Str(object? v) => v is null or DBNull ? null : Convert.ToString(v);
    }

    private sealed class FakeParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParams : DbParameterCollection
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

    private sealed class FakeReader(string[] cols, object?[][] rows) : DbDataReader
    {
        private int _i = -1;
        public override int FieldCount => cols.Length;
        public override bool HasRows => rows.Length > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => rows[_i][ordinal]!;
        public override object this[string name] => rows[_i][GetOrdinal(name)]!;
        public override bool Read() => ++_i < rows.Length;
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => rows[_i][ordinal] is null or DBNull;
        public override int GetOrdinal(string name) => Array.FindIndex(cols, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        public override string GetName(int ordinal) => cols[ordinal];
        public override string GetDataTypeName(int ordinal) => "text";
        public override Type GetFieldType(int ordinal) => rows[_i][ordinal]?.GetType() ?? typeof(string);
        public override object GetValue(int ordinal) => rows[_i][ordinal] ?? DBNull.Value;
        public override int GetValues(object[] values) { Array.Copy(rows[_i], values, FieldCount); return FieldCount; }
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_i][ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_i][ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_i][ordinal]);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_i][ordinal]);
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_i][ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_i][ordinal]);
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(rows[_i][ordinal]);
        public override string GetString(int ordinal) => Convert.ToString(rows[_i][ordinal])!;
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_i][ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_i][ordinal]);
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_i][ordinal]);
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();
    }
}
