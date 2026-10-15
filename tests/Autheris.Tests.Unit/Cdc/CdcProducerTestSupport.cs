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

        if (sql.StartsWith("SELECT TOP (1) 1 FROM", StringComparison.Ordinal))
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

    /// <summary>A second producer instance (own activation cache) on the same source and the same stream store.</summary>
    public MssqlChangeTrackingPoller CreateSecondPoller(FaultInjectingStreamStore? store = null)
    {
        var s = store ?? Store;
        var factory = new FakeSourceConnectionFactory(Source);
        var activator = new CdcTableActivator(Metadata, factory, s, Crypto, Monitor, NullLogger<CdcTableActivator>.Instance, null, Time);
        var builder = new CdcChangeEventBuilder(s, Crypto, Codec, Monitor, Time);
        return new MssqlChangeTrackingPoller(factory, activator, s, builder, Signal, Monitor, NullLogger<MssqlChangeTrackingPoller>.Instance, Time);
    }

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
