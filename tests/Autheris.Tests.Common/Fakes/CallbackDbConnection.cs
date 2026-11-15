namespace Autheris.Tests.Common.Fakes;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Pure in-memory <see cref="DbConnection"/> double driven by a caller-supplied command callback
/// so unit tests exercise real ADO.NET parameter binding and row hydration without a DB host.
/// </summary>
public sealed class CallbackDbConnection(
    Func<string, IReadOnlyDictionary<string, object?>, DbDataReader> executeReader,
    Func<CancellationToken, Task>? onOpenAsync = null) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "stub";
    public override string DataSource => "stub";
    public override string ServerVersion => "16.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open()
    {
        onOpenAsync?.Invoke(CancellationToken.None).GetAwaiter().GetResult();
        _state = ConnectionState.Open;
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (onOpenAsync is not null)
        {
            await onOpenAsync(cancellationToken).ConfigureAwait(false);
        }

        _state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new NoOpTransaction(this, isolationLevel);

    protected override DbCommand CreateDbCommand() =>
        new CallbackDbCommand(this, executeReader);

    private sealed class NoOpTransaction(DbConnection conn, IsolationLevel level) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class CallbackDbCommand(
        DbConnection conn,
        Func<string, IReadOnlyDictionary<string, object?>, DbDataReader> executeReader) : DbCommand
    {
        private readonly StubDbParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar()
        {
            using var reader = ExecuteDbDataReader(CommandBehavior.Default);
            return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
        }

        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new StubDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            executeReader(CommandText, _parameters.ToReadOnlyDictionary());
    }

    private sealed class StubDbParameter : DbParameter
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

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public IReadOnlyDictionary<string, object?> ToReadOnlyDictionary()
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in _items)
            {
                dict[p.ParameterName] = p.Value;
            }

            return dict;
        }

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (DbParameter value in values)
            {
                _items.Add(value);
            }
        }

        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName)
        {
            var idx = IndexOf(parameterName);
            if (idx >= 0) _items.RemoveAt(idx);
        }

        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }
}

[SuppressMessage("Design", "CA1010:Generic interface should also be implemented", Justification = "Derives from BCL DbDataReader.")]
public sealed class StubDbDataReader(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows) : DbDataReader
{
    private int _index = -1;

    public override int FieldCount => columns.Count;
    public override bool HasRows => rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    private object? Raw(int ordinal) => rows[_index][ordinal];

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Raw(ordinal), CultureInfo.InvariantCulture);
    public override byte GetByte(int ordinal) => Convert.ToByte(Raw(ordinal), CultureInfo.InvariantCulture);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => Convert.ToChar(Raw(ordinal), CultureInfo.InvariantCulture);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => Raw(ordinal)?.GetType().Name ?? "object";
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(Raw(ordinal), CultureInfo.InvariantCulture);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(Raw(ordinal), CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => Convert.ToDouble(Raw(ordinal), CultureInfo.InvariantCulture);
    public override Type GetFieldType(int ordinal) => Raw(ordinal)?.GetType() ?? typeof(object);
    public override float GetFloat(int ordinal) => Convert.ToSingle(Raw(ordinal), CultureInfo.InvariantCulture);
    public override Guid GetGuid(int ordinal) => Raw(ordinal) is Guid g ? g : Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => Convert.ToInt16(Raw(ordinal), CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => Convert.ToInt32(Raw(ordinal), CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => Convert.ToInt64(Raw(ordinal), CultureInfo.InvariantCulture);
    public override string GetName(int ordinal) => columns[ordinal];

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown column name.");
    }

    public override string GetString(int ordinal) =>
        Raw(ordinal) switch
        {
            string s => s,
            null or DBNull => throw new InvalidCastException($"Column {ordinal} is NULL."),
            var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
        };

    public override T GetFieldValue<T>(int ordinal)
    {
        var raw = Raw(ordinal);
        if (raw is T typed)
        {
            return typed;
        }

        if (typeof(T) == typeof(DateTimeOffset) && raw is string s)
        {
            return (T)(object)DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
        }

        return base.GetFieldValue<T>(ordinal);
    }

    public override object GetValue(int ordinal) => Raw(ordinal) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => Raw(ordinal) is null or DBNull;
    public override bool NextResult() => false;

    public override bool Read()
    {
        if (_index + 1 >= rows.Count)
        {
            return false;
        }

        _index++;
        return true;
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
