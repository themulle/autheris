namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
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
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class MssqlChangeTrackingPollerTests
{
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");

    private sealed record Change(long Version, string Operation, int Id);

    /// <summary>A scripted change tracking source answering the poller's T-SQL by recognising its statements.</summary>
    private sealed class FakeChangeSource
    {
        public List<Change> Changes { get; } = [];

        public IReadOnlyList<string> PrimaryKey { get; set; } = ["Id"];

        public bool PkQueryFails { get; set; }

        public long? MinValidVersion { get; set; } = 0;

        public long? CurrentVersion { get; set; } = 0;

        public object Respond(string sql, IReadOnlyDictionary<string, object?> parameters)
        {
            if (sql.Contains("sys.indexes", StringComparison.Ordinal))
            {
                if (PkQueryFails)
                {
                    throw new InvalidOperationException("transient pk query failure");
                }

                var pk = new DataTable();
                pk.Columns.Add("name", typeof(string));
                foreach (var column in PrimaryKey)
                {
                    pk.Rows.Add(column);
                }

                return pk;
            }

            if (sql.Contains("CHANGE_TRACKING_MIN_VALID_VERSION", StringComparison.Ordinal))
            {
                return MinValidVersion.HasValue ? MinValidVersion.Value : DBNull.Value;
            }

            if (sql.Contains("CHANGE_TRACKING_CURRENT_VERSION", StringComparison.Ordinal))
            {
                return CurrentVersion.HasValue ? CurrentVersion.Value : DBNull.Value;
            }

            var table = new DataTable();
            table.Columns.Add("SYS_CHANGE_VERSION", typeof(long));
            table.Columns.Add("SYS_CHANGE_OPERATION", typeof(string));
            foreach (var column in PrimaryKey)
            {
                table.Columns.Add("PK_" + column, column == "Id" ? typeof(int) : typeof(string));
            }

            table.Columns.Add("Id", typeof(int));
            table.Columns.Add("Name", typeof(string));

            IEnumerable<Change> selected;
            if (parameters.TryGetValue("@version", out var exact))
            {
                selected = Changes.Where(c => c.Version == Convert.ToInt64(exact));
            }
            else
            {
                var last = Convert.ToInt64(parameters["@lastVersion"]);
                var batch = Convert.ToInt32(parameters["@batchSize"]);
                selected = Changes.Where(c => c.Version > last).OrderBy(c => c.Version).ThenBy(c => c.Id).Take(batch);
            }

            foreach (var change in selected)
            {
                var values = new List<object> { change.Version, change.Operation };
                values.AddRange(PrimaryKey.Select(c => c == "Id" ? (object)change.Id : "k" + change.Id));
                values.Add(change.Id);
                values.Add("row" + change.Id);
                table.Rows.Add(values.ToArray());
            }

            return table;
        }
    }

    private sealed class RecordingChannel : ICdcEventChannel
    {
        public List<CdcEvent> Published { get; } = [];

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
        {
            Published.Add(cdcEvent);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static MssqlChangeTrackingPoller CreateWith(FakeChangeSource source, IMssqlWatermarkStore store)
    {
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new FakeConnection(source.Respond)));
        var options = Options.Create(new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions { Enabled = true, ConnectionString = "Server=localhost;Database=Test;" }
        });
        return new MssqlChangeTrackingPoller(factory, store, new RecordingChannel(), options, NullLogger<MssqlChangeTrackingPoller>.Instance);
    }

    private static (MssqlChangeTrackingPoller Poller, InMemoryMssqlWatermarkStore Store, RecordingChannel Channel) Create(
        FakeChangeSource source, int batchSize = 500)
    {
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new FakeConnection(source.Respond)));
        var store = new InMemoryMssqlWatermarkStore();
        var channel = new RecordingChannel();
        var options = Options.Create(new GatewayOptions
        {
            MssqlChangeTracking = new MssqlChangeTrackingOptions
            {
                Enabled = true,
                ConnectionString = "Server=localhost;Database=Test;",
                BatchSize = batchSize
            }
        });
        var poller = new MssqlChangeTrackingPoller(factory, store, channel, options, NullLogger<MssqlChangeTrackingPoller>.Instance);
        return (poller, store, channel);
    }

    [Fact]
    public async Task WatermarkBelowMinValid_Throws_NoReplay()
    {
        var source = new FakeChangeSource { MinValidVersion = 10 };
        source.Changes.Add(new Change(12, "I", 1));
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        var ex = await Should.ThrowAsync<CdcWatermarkExpiredException>(() => poller.PollTableChangesAsync(Table, 1));

        ex.StoredVersion.ShouldBe(5);
        ex.MinValidVersion.ShouldBe(10);
        channel.Published.ShouldBeEmpty();
        (await store.GetWatermarkAsync(Table)).ShouldBe(5);
    }

    [Fact]
    public async Task NoPrimaryKey_Throws()
    {
        var source = new FakeChangeSource { PrimaryKey = [] };
        source.Changes.Add(new Change(12, "I", 1));
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        await Should.ThrowAsync<CdcSourceConfigurationException>(() => poller.PollTableChangesAsync(Table, 1));

        channel.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstRun_StartsAtCurrentVersion()
    {
        var source = new FakeChangeSource { CurrentVersion = 100 };
        source.Changes.Add(new Change(3, "I", 1));
        source.Changes.Add(new Change(50, "U", 2));
        var (poller, store, channel) = Create(source);

        var processed = await poller.PollTableChangesAsync(Table, 1);

        processed.ShouldBe(0);
        channel.Published.ShouldBeEmpty();
        (await store.GetWatermarkAsync(Table)).ShouldBe(100);
    }

    [Fact]
    public async Task FencedCommit_ThrowsFencingTokenRejected()
    {
        var source = new FakeChangeSource();
        source.Changes.Add(new Change(12, "I", 1));
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 9); // a newer lease owner already wrote the watermark

        await Should.ThrowAsync<FencingTokenRejectedException>(() => poller.PollTableChangesAsync(Table, 4));

        channel.Published.ShouldBeEmpty(); // a fenced poll publishes nothing

        (await store.GetWatermarkAsync(Table)).ShouldBe(5);
    }

    [Fact]
    public async Task FullBatch_DoesNotSkipRowsOfSplitVersion()
    {
        var source = new FakeChangeSource();
        source.Changes.Add(new Change(10, "I", 1));
        source.Changes.Add(new Change(10, "I", 2));
        foreach (var id in new[] { 3, 4, 5, 6 })
        {
            source.Changes.Add(new Change(11, "U", id)); // one version with more rows than a batch
        }

        var (poller, store, channel) = Create(source, batchSize: 3);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        var first = await poller.PollTableChangesAsync(Table, 1);
        (await store.GetWatermarkAsync(Table)).ShouldBe(10); // version 11 was cut by the batch: not committed yet
        var second = await poller.PollTableChangesAsync(Table, 1);
        var third = await poller.PollTableChangesAsync(Table, 1);

        first.ShouldBe(2);
        second.ShouldBe(4); // the whole version is fetched without TOP
        third.ShouldBe(0);
        channel.Published.Select(e => e.EventId).Distinct().Count().ShouldBe(6);
        (await store.GetWatermarkAsync(Table)).ShouldBe(11);
    }

    [Fact]
    public async Task MinValidVersionNull_ThrowsConfigurationException()
    {
        var source = new FakeChangeSource { MinValidVersion = null };
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        await Should.ThrowAsync<CdcSourceConfigurationException>(() => poller.PollTableChangesAsync(Table, 1));

        channel.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransientPrimaryKeyQueryFailure_TakesNormalPath_NotConfigurationError()
    {
        var source = new FakeChangeSource { PkQueryFails = true };
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        var processed = await poller.PollTableChangesAsync(Table, 1);

        processed.ShouldBe(0);
        channel.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstRun_FencedCommit_ThrowsFencingTokenRejected()
    {
        var source = new FakeChangeSource { CurrentVersion = 100 };
        var (poller, _, _) = Create(source);
        var store = Substitute.For<IMssqlWatermarkStore>();
        store.GetWatermarkAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<long?>((long?)null));
        store.TryAdvanceWatermarkAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));
        var fenced = CreateWith(source, store);

        await Should.ThrowAsync<FencingTokenRejectedException>(() => fenced.PollTableChangesAsync(Table, 1));
    }

    [Fact]
    public async Task CompositePrimaryKey_EventIdJoinsAllKeyValues()
    {
        var source = new FakeChangeSource { PrimaryKey = ["Id", "Region"] };
        source.Changes.Add(new Change(10, "I", 7));
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        await poller.PollTableChangesAsync(Table, 1);

        channel.Published.Single().EventId.ShouldEndWith("_10_7|k7");
    }

    [Fact]
    public async Task PartialBatch_CommitsMaxVersion()
    {
        var source = new FakeChangeSource();
        source.Changes.Add(new Change(10, "I", 1));
        source.Changes.Add(new Change(11, "D", 2));
        var (poller, store, channel) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);

        var processed = await poller.PollTableChangesAsync(Table, 1);

        processed.ShouldBe(2);
        channel.Published.Select(e => e.Operation).ShouldBe([CdcOperation.Insert, CdcOperation.Delete]);
        (await store.GetWatermarkAsync(Table)).ShouldBe(11);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var source = new FakeChangeSource();
        var (poller, store, _) = Create(source);
        await store.TryAdvanceWatermarkAsync(Table, 5, 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => poller.PollTableChangesAsync(Table, 1, cts.Token));
    }

    // ----- scripted ADO.NET fakes

    private sealed class FakeConnection(Func<string, IReadOnlyDictionary<string, object?>, object> responder) : DbConnection
    {
#pragma warning disable CS8765
        public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(this, responder);
    }

    private sealed class FakeCommand(DbConnection connection, Func<string, IReadOnlyDictionary<string, object?>, object> responder) : DbCommand
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

        public override object? ExecuteScalar() => Respond();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ((DataTable)Respond()).CreateDataReader();

        private object Respond() => responder(
            CommandText,
            _parameters.Items.ToDictionary(p => p.ParameterName, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FakeParameter : DbParameter
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

    private sealed class FakeParameterCollection : DbParameterCollection
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
}
