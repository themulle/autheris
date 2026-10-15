namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

/// <summary>An in-memory <see cref="ICdcStreamStore"/> with the fencing and compare-and-swap semantics of the durable stores.</summary>
internal sealed class FakeCdcStreamStore : ICdcStreamStore
{
    private readonly object _gate = new();

    public sealed class StreamState
    {
        public required CdcStreamId Id { get; init; }
        public required string Source { get; init; }
        public required TableIdentifier Table { get; init; }
        public CdcStreamStatus Status { get; set; } = CdcStreamStatus.Active;
        public string? ActivationCode { get; set; }
        public long Head { get; set; }
        public long Tail { get; set; } = 1;
        public long? Watermark { get; set; }
        public long WatermarkToken { get; set; }
        public long ProducerToken { get; set; }
        public DateTimeOffset? LastPolled { get; set; }
        public List<CdcStreamEventRecord> Events { get; } = [];
        public Dictionary<string, CdcRowImageRecord> Images { get; } = new(StringComparer.Ordinal);
    }

    public Dictionary<CdcStreamId, StreamState> Streams { get; } = [];

    public int AppendCalls { get; private set; }

    public int TouchCalls { get; private set; }

    public bool IsDurable => true;

    public StreamState Only => Streams.Values.Single();

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var state = Streams.Values.FirstOrDefault(s => s.Source == source && s.Table.Equals(table));
            if (state is null)
            {
                state = new StreamState { Id = CdcStreamId.New(), Source = source, Table = table };
                Streams[state.Id] = state;
            }

            return Task.FromResult(Describe(state));
        }
    }

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Streams.TryGetValue(id, out var s) ? Describe(s) : null);
        }
    }

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CdcStreamDescriptor>>(Streams.Values.Select(Describe).ToList());
        }
    }

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Streams[id].Status = status;
            Streams[id].ActivationCode = activationCode;
        }

        return Task.CompletedTask;
    }

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            TouchCalls++;
            Streams[id].LastPolled = at;
        }

        return Task.CompletedTask;
    }

    public Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            AppendCalls++;
            var s = Streams[batch.StreamId];
            if (batch.FencingToken < s.WatermarkToken || batch.FencingToken < s.ProducerToken)
            {
                throw new FencingTokenRejectedException(s.Table.ToString(), batch.FencingToken);
            }

            if (batch.ExpectedHeadOffset != s.Head)
            {
                throw new CdcStreamConflictException();
            }

            for (var i = 0; i < batch.Events.Count; i++)
            {
                if (batch.Events[i].Offset != s.Head + 1 + i)
                {
                    throw new InvalidOperationException("offsets must be dense");
                }
            }

            s.Watermark = Math.Max(s.Watermark ?? long.MinValue, batch.NewWatermark);
            s.WatermarkToken = batch.FencingToken;
            s.ProducerToken = batch.FencingToken;
            s.Head += batch.Events.Count;
            s.Events.AddRange(batch.Events);
            foreach (var w in batch.ImageWrites)
            {
                var key = Convert.ToHexString(w.KeyFingerprint);
                if (w.Image is null)
                {
                    s.Images.Remove(key);
                }
                else
                {
                    s.Images[key] = new CdcRowImageRecord(w.KeyFingerprint, w.TenantScope, w.TenantId, w.Image, w.Offset);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var s = Streams[id];
            return Task.FromResult<IReadOnlyList<CdcRowImageRecord>>(
                fingerprints.Select(f => s.Images.GetValueOrDefault(Convert.ToHexString(f))).Where(r => r is not null).Select(r => r!).ToList());
        }
    }

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var s = Streams[id];
            return Task.FromResult(new CdcReadResult(s.Tail, s.Head, s.Events.Where(e => e.Offset >= fromOffsetInclusive).Take(maxEvents).ToList()));
        }
    }

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) => throw new NotSupportedException();

    private static CdcStreamDescriptor Describe(StreamState s) =>
        new(s.Id, s.Source, s.Table, s.Status, s.ActivationCode, s.Head, s.Tail, s.Watermark, s.LastPolled);
}

/// <summary>Fault injection around a store: simulates a producer crash before or after the append transaction commits.</summary>
internal sealed class FaultInjectingStreamStore(ICdcStreamStore inner) : ICdcStreamStore
{
    public bool ThrowBeforeAppend { get; set; }

    public bool ThrowAfterAppend { get; set; }

    /// <summary>Runs just before the append reaches the inner store (another producer commits in between).</summary>
    public Func<Task>? BeforeAppend { get; set; }

    public bool IsDurable => inner.IsDurable;

    public Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default) => inner.EnsureStreamAsync(source, table, ct);

    public Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default) => inner.GetStreamAsync(id, ct);

    public Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default) => inner.ListStreamsAsync(ct);

    public Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default) => inner.SetStatusAsync(id, status, activationCode, ct);

    public Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default) => inner.TouchPolledAsync(id, at, ct);

    public async Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default)
    {
        if (ThrowBeforeAppend)
        {
            throw new InvalidOperationException("injected crash before the append commits");
        }

        if (BeforeAppend is { } hook)
        {
            await hook();
        }

        await inner.AppendAsync(batch, ct);
        if (ThrowAfterAppend)
        {
            throw new InvalidOperationException("injected crash after the append committed");
        }
    }

    public Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default) => inner.GetRowImagesAsync(id, fingerprints, ct);

    public Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default) => inner.ReadAsync(id, fromOffsetInclusive, maxEvents, ct);

    public Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default) => inner.PurgeAsync(id, newTailOffset, maxRows, fencingToken, ct);

    public Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default) => inner.PurgeImagesAsync(id, maxRows, ct);

    public Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default) => inner.DiscardUnreadableAsync(id, unreadableDekIds, ct);
}

/// <summary>Test crypto: AES-256-GCM with a fixed key and the offset/tenant bound into the AAD; HMAC fingerprints per stream.</summary>
internal sealed class TestCdcCrypto : ICdcStreamCrypto
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("test-cdc-kek"));

    public bool KeysUnavailable { get; set; }

    public ValueTask<CdcProtectedBlob> ProtectEventAsync(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        ValueTask.FromResult(Seal($"evt|{id.Value}|{offset}|{kind}|{scope}|{tenantId}|{priorTenantId}", plaintext.Span));

    public ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default) =>
        ValueTask.FromResult(Open($"evt|{record.StreamId.Value}|{record.Offset}|{record.Kind}|{record.TenantScope}|{record.TenantId}|{record.PriorTenantId}", record.Payload));

    public ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
        ValueTask.FromResult(Seal($"img|{id.Value}|{Convert.ToHexString(fingerprint)}", plaintext.Span));

    public ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default) =>
        ValueTask.FromResult(Open($"img|{id.Value}|{Convert.ToHexString(image.KeyFingerprint)}", image.Image));

    public ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default)
    {
        if (KeysUnavailable)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var canonical = id.Value + "|" + string.Join("|", keyValues.Select(v => $"{v?.GetType().Name}:{v}".Length + ":" + $"{v?.GetType().Name}:{v}"));
        return ValueTask.FromResult(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(canonical)));
    }

    private CdcProtectedBlob Seal(string aad, ReadOnlySpan<byte> plaintext)
    {
        if (KeysUnavailable)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var gcm = new AesGcm(Key, 16);
        gcm.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return new CdcProtectedBlob("dek-test", [.. nonce, .. tag, .. cipher]);
    }

    private static byte[] Open(string aad, CdcProtectedBlob blob)
    {
        var data = blob.Data;
        var plain = new byte[data.Length - 28];
        using var gcm = new AesGcm(Key, 16);
        gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }
}

/// <summary>A tagged-JSON test codec; a value of an unknown CLR type is <see cref="NotSupportedException"/> like the closed-tag production codec.</summary>
internal sealed class TestPayloadCodec : ICdcPayloadCodec
{
    public byte[] SerializeChange(CdcChangePayload payload) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["key"] = Encode(payload.Key),
        ["before"] = payload.Before is null ? null : Encode(payload.Before),
        ["after"] = payload.After is null ? null : Encode(payload.After),
        ["beforeUnknown"] = payload.BeforeUnknown,
        ["oversize"] = payload.PayloadOversize
    });

    public byte[] SerializeControl(CdcControlPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload);

    public byte[] SerializeImage(IReadOnlyDictionary<string, object?> row) => JsonSerializer.SerializeToUtf8Bytes(Encode(row));

    public IReadOnlyDictionary<string, object?> DeserializeImage(ReadOnlySpan<byte> plaintext) =>
        Decode(JsonSerializer.Deserialize<Dictionary<string, string?>>(plaintext)!);

    public static CdcChangePayload ReadChange(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var root = doc.RootElement;
        IReadOnlyDictionary<string, object?>? Obj(string name) =>
            root.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : Decode(root.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString()));
        return new CdcChangePayload(Obj("key")!, Obj("before"), Obj("after"), root.GetProperty("beforeUnknown").GetBoolean(), root.GetProperty("oversize").GetBoolean());
    }

    private static Dictionary<string, string?> Encode(IReadOnlyDictionary<string, object?> row) =>
        row.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            null => null,
            int i => "i32:" + i,
            long l => "i64:" + l,
            string s => "s:" + s,
            bool b => "b:" + b,
            decimal d => "dec:" + d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(kv.Value.GetType().Name)
        });

    private static Dictionary<string, object?> Decode(Dictionary<string, string?> encoded) =>
        encoded.ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : DecodeValue(kv.Value));

    private static object DecodeValue(string v)
    {
        var i = v.IndexOf(':');
        var tag = v[..i];
        var text = v[(i + 1)..];
        return tag switch
        {
            "i32" => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "i64" => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            "s" => text,
            "b" => bool.Parse(text),
            "dec" => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(tag)
        };
    }
}

/// <summary>A provider exception with a SQL Server style error number.</summary>
internal sealed class FakeSqlException(int number) : DbException("fake sql error")
{
    public int Number { get; } = number;
}

/// <summary>A scripted change tracking source answering the producer's T-SQL by recognising its statements.</summary>
internal sealed class FakeChangeSource
{
    public sealed record Change(long Version, string Operation, Dictionary<string, object?> Values);

    public List<Change> Changes { get; } = [];

    public IReadOnlyList<string> ChangeTableKey { get; set; } = ["Id"];

    public IReadOnlyList<string> PrimaryKey => ChangeTableKey;

    public long? MinValidVersion { get; set; } = 0;

    public long? CurrentVersion { get; set; } = 0;

    public bool SnapshotOff { get; set; }

    public bool ThrowOnRead { get; set; }

    public List<string> Statements { get; } = [];

    public List<IsolationLevel> Isolations { get; } = [];

    public List<int> CommandTimeouts { get; } = [];

    public int Commits { get; set; }

    public int Opened { get; set; }

    public Action<string>? OnStatement { get; set; }

    public object? Execute(string sql, IReadOnlyDictionary<string, object?> p, int commandTimeout)
    {
        Statements.Add(sql);
        CommandTimeouts.Add(commandTimeout);
        OnStatement?.Invoke(sql);

        if (sql.Contains("CHANGE_TRACKING_MIN_VALID_VERSION", StringComparison.Ordinal))
        {
            return MinValidVersion.HasValue ? MinValidVersion.Value : DBNull.Value;
        }

        if (sql.Contains("CHANGE_TRACKING_CURRENT_VERSION", StringComparison.Ordinal))
        {
            return CurrentVersion.HasValue ? CurrentVersion.Value : DBNull.Value;
        }

        if (sql.StartsWith("SELECT TOP (0) *", StringComparison.Ordinal))
        {
            var t = new DataTable();
            foreach (var c in new[] { "SYS_CHANGE_VERSION", "SYS_CHANGE_CREATION_VERSION", "SYS_CHANGE_OPERATION", "SYS_CHANGE_COLUMNS", "SYS_CHANGE_CONTEXT" })
            {
                t.Columns.Add(c, typeof(object));
            }

            foreach (var k in ChangeTableKey)
            {
                t.Columns.Add(k, typeof(object));
            }

            return t;
        }

        if (sql.StartsWith("SELECT TOP (0) 1 FROM", StringComparison.Ordinal))
        {
            if (SnapshotOff)
            {
                throw new FakeSqlException(3952);
            }

            return null;
        }

        if (ThrowOnRead)
        {
            throw new InvalidOperationException("transient source failure");
        }

        var selectList = sql[sql.IndexOf(" END", StringComparison.Ordinal)..sql.IndexOf(" FROM ", StringComparison.Ordinal)];
        var columns = Regex.Matches(selectList, @"T\.\[(\w+)\]").Select(m => m.Groups[1].Value).ToList();
        var table = new DataTable();
        table.Columns.Add("SYS_CHANGE_VERSION", typeof(long));
        table.Columns.Add("SYS_CHANGE_OPERATION", typeof(string));
        foreach (var k in PrimaryKey)
        {
            table.Columns.Add("CT_" + k, typeof(object));
        }

        table.Columns.Add("PRESENT", typeof(int));
        foreach (var c in columns)
        {
            table.Columns.Add("T_" + c, typeof(object));
        }

        IEnumerable<Change> selected;
        if (p.TryGetValue("@version", out var exact))
        {
            selected = Changes.Where(c => c.Version == Convert.ToInt64(exact));
        }
        else
        {
            var last = Convert.ToInt64(p["@lastVersion"]);
            var batch = Convert.ToInt32(p["@batchSize"]);
            selected = Changes.Where(c => c.Version > last).OrderBy(c => c.Version).ThenBy(c => Convert.ToString(c.Values[PrimaryKey[0]])).Take(batch);
        }

        foreach (var change in selected)
        {
            var isDelete = string.Equals(change.Operation, "D", StringComparison.OrdinalIgnoreCase);
            var values = new List<object?> { change.Version, change.Operation };
            values.AddRange(PrimaryKey.Select(k => change.Values[k]));
            values.Add(isDelete ? 0 : 1);
            values.AddRange(columns.Select(c => isDelete ? null : change.Values.GetValueOrDefault(c)));
            table.Rows.Add(values.Select(v => v ?? DBNull.Value).ToArray());
        }

        return table;
    }
}

internal sealed class FakeSourceConnectionFactory(FakeChangeSource source) : ISqlConnectionFactory
{
    public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        source.Opened++;
        return Task.FromResult<DbConnection>(new FakeConnection(source));
    }
}

internal sealed class FakeConnection(FakeChangeSource source) : DbConnection
{
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => ConnectionState.Open;

    public FakeChangeSource Source => source;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        source.Isolations.Add(isolationLevel);
        return new FakeTransaction(this, isolationLevel);
    }

    protected override DbCommand CreateDbCommand() => new FakeCommand(this);
}

internal sealed class FakeTransaction(FakeConnection connection, IsolationLevel level) : DbTransaction
{
    public override IsolationLevel IsolationLevel => level;

    protected override DbConnection DbConnection => connection;

    public override void Commit() => connection.Source.Commits++;

    public override void Rollback()
    {
    }
}

internal sealed class FakeCommand(FakeConnection connection) : DbCommand
{
    private readonly FakeParameterCollection _parameters = new();

#pragma warning disable CS8765
    public override string CommandText { get; set; } = string.Empty;
#pragma warning restore CS8765

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get => connection; set { } }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    public override object? ExecuteScalar()
    {
        var result = Respond();
        return result is DataTable ? null : result;
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        Respond() is DataTable table ? table.CreateDataReader() : throw new NotSupportedException("scalar statement executed as reader");

    private object? Respond() => connection.Source.Execute(
        CommandText,
        _parameters.Items.ToDictionary(p => p.ParameterName, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase),
        CommandTimeout);
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

#pragma warning disable CS8765
    public override string ParameterName { get; set; } = string.Empty;
#pragma warning restore CS8765

    public override int Size { get; set; }

#pragma warning disable CS8765
    public override string SourceColumn { get; set; } = string.Empty;
#pragma warning restore CS8765

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeParameterCollection : DbParameterCollection
{
    public List<DbParameter> Items { get; } = [];

    public override int Count => Items.Count;

    public override object SyncRoot => ((ICollection)Items).SyncRoot;

    public override int Add(object value)
    {
        Items.Add((DbParameter)value);
        return Items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => Items.Clear();

    public override bool Contains(object value) => Items.Contains((DbParameter)value);

    public override bool Contains(string value) => Items.Any(p => p.ParameterName == value);

    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => Items.GetEnumerator();

    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);

    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => Items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => Items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => Items.RemoveAll(p => p.ParameterName == parameterName);

    protected override DbParameter GetParameter(int index) => Items[index];

    protected override DbParameter GetParameter(string parameterName) => Items.First(p => p.ParameterName == parameterName);

    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
}

/// <summary>A signal that records the heads it was told about.</summary>
internal sealed class RecordingStreamSignal : ICdcStreamSignal
{
    public List<(CdcStreamId Id, long Head)> Notified { get; } = [];

    public bool Throws { get; set; }

    public ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default)
    {
        if (Throws)
        {
            throw new InvalidOperationException("signal down");
        }

        Notified.Add((id, headOffset));
        return ValueTask.CompletedTask;
    }

    public IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced) => throw new NotSupportedException();
}

internal sealed class CdcProducerHarness
{
    public static readonly TableIdentifier TableId = new("finance", "dbo", "invoices");

    public FakeChangeSource Source { get; } = new();

    public FakeCdcStreamStore InnerStore { get; } = new();

    public FaultInjectingStreamStore Store { get; }

    public TestCdcCrypto Crypto { get; } = new();

    public TestPayloadCodec Codec { get; } = new();

    public RecordingStreamSignal Signal { get; } = new();

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new();

    public Autheris.Application.Interfaces.ITableMetadataRepository Metadata { get; } = NSubstitute.Substitute.For<Autheris.Application.Interfaces.ITableMetadataRepository>();

    public GatewayOptions Options { get; }

    public MutableGatewayOptionsMonitor Monitor { get; }

    public CdcTableActivator Activator { get; }

    public CdcChangeEventBuilder Builder { get; }

    public MssqlChangeTrackingPoller Poller { get; }

    public CdcProducerHarness(
        Func<MssqlChangeTrackingOptions, MssqlChangeTrackingOptions>? configure = null,
        TableMetadata? metadata = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? catalogVersion = null,
        FaultInjectingStreamStore? store = null,
        bool requireTenantColumn = false,
        CdcStreamOptions? cdcStream = null)
    {
        Store = store ?? new FaultInjectingStreamStore(InnerStore);
        var cdc = new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"] };
        Options = new GatewayOptions
        {
            MssqlChangeTracking = configure?.Invoke(cdc) ?? cdc,
            CdcStream = cdcStream ?? new CdcStreamOptions(),
            DataSources = new SqlDataSourceOptions
            {
                RequireTenantColumn = requireTenantColumn,
                Connections = { ["erp"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=fake;Database=erp" } }
            }
        };
        Monitor = new MutableGatewayOptionsMonitor(Options);
        Metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<TableMetadata?>(MetadataFor ?? metadata ?? Meta()));
        var factory = new FakeSourceConnectionFactory(Source);
        Activator = new CdcTableActivator(Metadata, factory, Store, Crypto, Monitor, NullLogger<CdcTableActivator>.Instance, catalogVersion, Time);
        Builder = new CdcChangeEventBuilder(Store, Crypto, Codec, Monitor, Time);
        Poller = new MssqlChangeTrackingPoller(factory, Activator, Store, Builder, Signal, Monitor, NullLogger<MssqlChangeTrackingPoller>.Instance, Time);
    }

    public TableMetadata? MetadataFor { get; set; }

    public static TableMetadata Meta(
        IEnumerable<string>? columns = null,
        IEnumerable<string>? pk = null,
        string sourceType = "SqlServer",
        string sourceName = "erp",
        bool active = true,
        string? owner = null) => new()
    {
        Table = new Table { SourceType = sourceType, SourceName = sourceName, SchemaName = "dbo", TableName = "invoices", IsActive = active },
        Identifier = TableId,
        OwnerTenantId = owner,
        Columns = (columns ?? ["Id", "Name"]).Select(c => new TableColumn { ColumnName = c }).ToList(),
        PrimaryKeyColumns = (pk ?? ["Id"]).ToList()
    };

    public static FakeChangeSource.Change Change(long version, string op, int id, string? name = null, string? tenant = null)
    {
        var values = new Dictionary<string, object?> { ["Id"] = id, ["Name"] = name ?? "row" + id };
        if (tenant is not null)
        {
            values["TenantId"] = tenant;
        }

        return new FakeChangeSource.Change(version, op, values);
    }

    /// <summary>The decrypted change payloads of the single stream, in offset order.</summary>
    public async Task<List<(CdcStreamEventRecord Record, CdcChangePayload Payload)>> ReadChangesAsync()
    {
        var list = new List<(CdcStreamEventRecord, CdcChangePayload)>();
        foreach (var e in InnerStore.Only.Events.Where(e => e.Kind == CdcStreamEventKind.Change))
        {
            list.Add((e, TestPayloadCodec.ReadChange(await Crypto.UnprotectEventAsync(e))));
        }

        return list;
    }

    /// <summary>Seeds a watermark so the first poll does not just start at the current version.</summary>
    public async Task SeedWatermarkAsync(long watermark, long token = 1)
    {
        var stream = await Store.EnsureStreamAsync(CdcTableActivator.StreamSource, TableId);
        await Store.AppendAsync(new CdcAppendBatch(stream.Id, CdcTableActivator.StreamSource, TableId, token, stream.HeadOffset, watermark, [], []));
    }
}

/// <summary>An options monitor whose value tests can replace (options reload).</summary>
internal sealed class MutableGatewayOptionsMonitor(GatewayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>
{
    public GatewayOptions CurrentValue { get; set; } = value;

    public GatewayOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
}
