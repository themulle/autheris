namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.Cdc.CustomQuery;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CustomQueryPollerServiceTests
{
    private readonly InMemoryCdcStreamStore _streamStore = new();
    private readonly ICdcStreamSignal _signal = Substitute.For<ICdcStreamSignal>();
    private readonly IDistributedClusterStateProvider _clusterState = Substitute.For<IDistributedClusterStateProvider>();
    private readonly CdcChangeEventBuilder _builder;

    public CustomQueryPollerServiceTests()
    {
        var crypto = Substitute.For<ICdcStreamCrypto>();
        crypto.FingerprintAsync(Arg.Any<CdcStreamId>(), Arg.Any<IReadOnlyList<object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new byte[32]));
        crypto.ProtectEventAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<CdcStreamEventKind>(), Arg.Any<CdcTenantScope>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(6).ToArray())));
        crypto.ProtectImageAsync(Arg.Any<CdcStreamId>(), Arg.Any<byte[]>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new CdcProtectedBlob("dek1", call.ArgAt<ReadOnlyMemory<byte>>(2).ToArray())));

        var codec = Substitute.For<ICdcPayloadCodec>();
        codec.SerializeChange(Arg.Any<CdcChangePayload>()).Returns(new byte[16]);
        codec.SerializeImage(Arg.Any<IReadOnlyDictionary<string, object?>>()).Returns(new byte[16]);

        var gatewayOpt = Substitute.For<IOptionsMonitor<GatewayOptions>>();
        gatewayOpt.CurrentValue.Returns(new GatewayOptions());

        _builder = new CdcChangeEventBuilder(_streamStore, crypto, codec, gatewayOpt);
    }

    private static CustomQueryPollerStreamOptions SamplePoller() => new()
    {
        StreamName = "crm_customers",
        Table = "crm.dbo.customers",
        DataSource = "crm_db",
        QueryTemplate = "SELECT id, name, updated_at FROM customers WHERE (updated_at > @p_wm_updated_at OR id > @p_wm_id) ORDER BY updated_at ASC, id ASC",
        KeyColumns = ["id"],
        Watermarks =
        [
            new() { Name = "updated_at", Type = WatermarkDimensionType.DateTimeOffset, ParameterName = "p_wm_updated_at", Column = "updated_at", InitialValue = "1970-01-01T00:00:00Z" },
            new() { Name = "id", Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_id", Column = "id", InitialValue = "0" }
        ],
        BatchSize = 100,
        PollingIntervalMilliseconds = 1000
    };

    [Fact]
    public void GetPartitions_WhenDisabled_ReturnsEmpty()
    {
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = false, Pollers = [SamplePoller()] });

        var factory = Substitute.For<IDataSourceConnectionFactory>();
        var service = new TestCustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState);

        var partitions = service.ExposePartitions();
        partitions.ShouldBeEmpty();
    }

    [Fact]
    public void GetPartitions_WhenEnabled_ReturnsPartitionsWithLeaseKeys()
    {
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = true, Pollers = [SamplePoller()] });

        var factory = Substitute.For<IDataSourceConnectionFactory>();
        var service = new TestCustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState);

        var partitions = service.ExposePartitions();
        partitions.Count.ShouldBe(1);
        partitions[0].Key.ShouldBe("cdc:query:poll:crm_customers");
        partitions[0].Schedule.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task PollStreamBatchAsync_ReadsRowsAndCommitsAppendBatchWithWatermarkState()
    {
        var poller = SamplePoller();
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });

        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1L, ["name"] = "Alice", ["updated_at"] = "2026-10-19T05:00:00Z" },
            new() { ["id"] = 2L, ["name"] = "Bob", ["updated_at"] = "2026-10-19T06:00:00Z" }
        };

        var conn = new FakeDbConnection(rows);
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync(poller.DataSource, Arg.Any<CancellationToken>()).Returns(conn);

        var service = new CustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState, NullLogger<CustomQueryPollerService>.Instance);

        var count = await service.PollStreamBatchAsync(poller, fencingToken: 1);

        count.ShouldBe(2);

        // Verify watermark state in store
        var desc = await _streamStore.EnsureStreamAsync("query_poller", TableIdentifier.Parse(poller.Table));
        var vectorRecord = await _streamStore.GetWatermarkVectorAsync(desc.Id);
        vectorRecord.ShouldNotBeNull();
        vectorRecord.Vector.GetValue("id").ShouldBe("2");
        vectorRecord.Vector.GetValue("updated_at").ShouldBe("2026-10-19T06:00:00Z");

        // Verify signal called
        await _signal.Received(1).NotifyAdvancedAsync(desc.Id, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollStreamBatchAsync_EmptyRows_TouchesPolledAndReturnsZero()
    {
        var poller = SamplePoller();
        var monitor = Substitute.For<IOptionsMonitor<CustomQueryPollerOptions>>();
        monitor.CurrentValue.Returns(new CustomQueryPollerOptions { Enabled = true, Pollers = [poller] });

        var conn = new FakeDbConnection([]);
        var factory = Substitute.For<IDataSourceConnectionFactory>();
        factory.CreateConnectionAsync(poller.DataSource, Arg.Any<CancellationToken>()).Returns(conn);

        var service = new CustomQueryPollerService(monitor, _streamStore, _builder, factory, _signal, _clusterState, NullLogger<CustomQueryPollerService>.Instance);

        var count = await service.PollStreamBatchAsync(poller, fencingToken: 1);

        count.ShouldBe(0);
        await _signal.DidNotReceive().NotifyAdvancedAsync(Arg.Any<CdcStreamId>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private sealed class TestCustomQueryPollerService : CustomQueryPollerService
    {
        public TestCustomQueryPollerService(
            IOptionsMonitor<CustomQueryPollerOptions> options,
            ICdcStreamStore streamStore,
            CdcChangeEventBuilder eventBuilder,
            IDataSourceConnectionFactory dataSourceConnectionFactory,
            ICdcStreamSignal signal,
            IDistributedClusterStateProvider clusterState)
            : base(options, streamStore, eventBuilder, null!, signal, clusterState, NullLogger<CustomQueryPollerService>.Instance, null, null, dataSourceConnectionFactory)
        {
        }

        public IReadOnlyList<ClusterPartition> ExposePartitions() => GetPartitions();
    }

    private sealed class FakeDbConnection(List<Dictionary<string, object?>> rows) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "DataSource=:memory:";
        public override string Database => "TestDb";
        public override string DataSource => "TestDb";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this, rows);
    }

    private sealed class FakeDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeDbCommand(DbConnection connection, List<Dictionary<string, object?>> rows) : DbCommand
    {
        private readonly FakeDbParameterCollection _parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new FakeDbDataReader(rows);
    }

    private sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];
        public override int Count => _parameters.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _parameters.Add((DbParameter)value); return _parameters.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _parameters.Clear();
        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
        public override bool Contains(string value) => _parameters.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _parameters.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _parameters.RemoveAll(p => p.ParameterName == parameterName);
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => _parameters.First(p => p.ParameterName == parameterName);
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _parameters[IndexOf(parameterName)] = value;
    }

    private sealed class FakeDbDataReader(List<Dictionary<string, object?>> rows) : DbDataReader
    {
        private int _index = -1;
        private readonly List<string> _columns = rows.Count > 0 ? rows[0].Keys.ToList() : [];

        public override int FieldCount => _columns.Count;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => rows.Count;
        public override bool HasRows => rows.Count > 0;

        public override bool Read()
        {
            _index++;
            return _index < rows.Count;
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

        public override string GetName(int ordinal) => _columns[ordinal];
        public override int GetOrdinal(string name) => _columns.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        public override object GetValue(int ordinal) => rows[_index][_columns[ordinal]] ?? DBNull.Value;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool IsDBNull(int ordinal) => rows[_index][_columns[ordinal]] is null or DBNull;

        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "TEXT";
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override System.Collections.IEnumerator GetEnumerator() => rows.GetEnumerator();
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override string GetString(int ordinal) => GetValue(ordinal)?.ToString() ?? string.Empty;
        public override int GetValues(object[] values) => 0;
        public override bool NextResult() => false;
    }
}
