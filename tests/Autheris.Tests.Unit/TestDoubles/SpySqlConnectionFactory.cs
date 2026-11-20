using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class SpySqlConnectionFactory(Func<DataSourceConnectionOptions, SpyDbConnection>? createConnection = null)
    : ISqlConnectionFactory
{
    private readonly List<DataSourceConnectionOptions> _createCalls = [];

    public IReadOnlyList<DataSourceConnectionOptions> CreateCalls => _createCalls;

    public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _createCalls.Add(options);
        DbConnection conn = createConnection is not null ? createConnection(options) : new SpyDbConnection();
        return Task.FromResult(conn);
    }
}

internal sealed class SpyDbConnection : DbConnection
{
    private readonly Queue<SpyDbCommand> _queuedCommands = new();
    private readonly List<SpyDbCommand> _createdCommands = [];
    private ConnectionState _state = ConnectionState.Open;

    public SpyDbConnection(params SpyDbCommand[] commands)
    {
        foreach (var command in commands)
        {
            _queuedCommands.Enqueue(command);
        }
    }

    public IReadOnlyList<SpyDbCommand> CreatedCommands => _createdCommands;

    public SpyDbConnection Enqueue(SpyDbCommand command)
    {
        _queuedCommands.Enqueue(command);
        return this;
    }

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "test";
    public override string DataSource => "test";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand()
    {
        var cmd = _queuedCommands.Count > 0 ? _queuedCommands.Dequeue() : new SpyDbCommand();
        cmd.Connection = this;
        _createdCommands.Add(cmd);
        return cmd;
    }
}

internal sealed class SpyDbCommand : DbCommand
{
    private readonly SpyDbParameterCollection _parameters = new();

    public Func<CancellationToken, int>? OnExecuteNonQuery { get; set; }
    public Func<CommandBehavior, CancellationToken, DbDataReader>? OnExecuteReader { get; set; }
    public DbDataReader? Reader { get; set; }
    public int CancelCalls { get; private set; }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() => CancelCalls++;
    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new SpyDbParameter();

    public override int ExecuteNonQuery() => OnExecuteNonQuery?.Invoke(CancellationToken.None) ?? 0;

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => Task.FromResult(OnExecuteNonQuery?.Invoke(cancellationToken) ?? 0);

    public override object? ExecuteScalar() => null;

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => OnExecuteReader?.Invoke(behavior, CancellationToken.None) ?? Reader ?? new SpyDbDataReader();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => Task.FromResult(OnExecuteReader?.Invoke(behavior, cancellationToken) ?? Reader ?? new SpyDbDataReader());
}

internal sealed class SpyDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => _items;
    public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => value is DbParameter p && _items.Contains(p);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => value is DbParameter p ? _items.IndexOf(p) : -1;
    public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) { if (value is DbParameter p) _items.Remove(p); }
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) { var idx = IndexOf(parameterName); if (idx >= 0) _items.RemoveAt(idx); }
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}

internal sealed class SpyDbParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class SpyDbDataReader : DbDataReader
{
    public int ConfiguredFieldCount { get; init; }
    public bool ConfiguredHasRows { get; init; }
    public Func<CancellationToken, bool>? OnRead { get; init; }
    public Func<int, string>? OnGetName { get; init; }
    public Func<int, Type>? OnGetFieldType { get; init; }
    public Func<int, bool>? OnIsDBNull { get; init; }
    public Func<int, object>? OnGetValue { get; init; }
    public Func<int, long, char[]?, int, int, long>? OnGetChars { get; init; }

    public override int FieldCount => ConfiguredFieldCount;
    public override int Depth => 0;
    public override bool HasRows => ConfiguredHasRows;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => OnRead?.Invoke(CancellationToken.None) ?? false;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(OnRead?.Invoke(cancellationToken) ?? false);
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override string GetName(int ordinal) => OnGetName?.Invoke(ordinal) ?? $"col{ordinal}";
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override Type GetFieldType(int ordinal) => OnGetFieldType?.Invoke(ordinal) ?? typeof(object);
    public override bool IsDBNull(int ordinal) => OnIsDBNull?.Invoke(ordinal) ?? false;
    public override object GetValue(int ordinal) => OnGetValue?.Invoke(ordinal) ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => OnGetChars?.Invoke(ordinal, dataOffset, buffer, bufferOffset, length) ?? 0;
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal)) ?? string.Empty;
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);
}
