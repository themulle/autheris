namespace Autheris.Tests.Unit.Sql.Policy;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>A connection that records every command (text, transaction, parameters) and serves scripted key rows; no provider involved.</summary>
internal sealed class RecordingDbConnection : DbConnection
{
    public List<RecordedCommand> Commands { get; } = [];

    public List<RecordingTransaction> Transactions { get; } = [];

    /// <summary>Rows served by the command whose text starts with <see cref="QueryPrefix"/>.</summary>
    public List<object?> KeyRows { get; init; } = [];

    public string QueryPrefix { get; init; } = "SELECT";

    public Exception? ExecuteFailure { get; init; }

    public int? FailAfterRows { get; init; }

    public bool DropParameters { get; init; }

    public int RowsRead { get; private set; }

    public bool Cancelled { get; private set; }

    public bool Disposed { get; private set; }

    [AllowNull] public override string ConnectionString { get => string.Empty; set { } }

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() { }

    public override void Open() { }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var tx = new RecordingTransaction(this);
        Transactions.Add(tx);
        return tx;
    }

    protected override DbCommand CreateDbCommand() => new RecordingCommand(this);

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    internal void ReadRow() => RowsRead++;

    internal void MarkCancelled() => Cancelled = true;

    internal sealed record RecordedCommand(string Text, bool InTransaction, IReadOnlyList<(string Name, object? Value, DbType Type)> Parameters, int Timeout);

    internal sealed class RecordingTransaction(RecordingDbConnection connection) : DbTransaction
    {
        public bool RolledBack { get; private set; }

        public bool Committed { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() => Committed = true;

        public override void Rollback() => RolledBack = true;
    }

    private sealed class RecordingCommand(RecordingDbConnection owner) : DbCommand
    {
        private readonly RecordingParameters _parameters = new(owner.DropParameters);

        [AllowNull] public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; } = 30;

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get => owner; set { } }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        /// <summary>The Oracle binder insists on this property of ODP.NET commands (SEC-ADG-03).</summary>
        public bool BindByName { get; set; }

        public override void Cancel() => owner.MarkCancelled();

        public override int ExecuteNonQuery()
        {
            Record();
            return 0;
        }

        public override object? ExecuteScalar()
        {
            Record();
            return null;
        }

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new RecordingParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            if (owner.ExecuteFailure is { } failure)
            {
                throw failure;
            }

            return new RowReader(owner);
        }

        private void Record() => owner.Commands.Add(new RecordedCommand(
            CommandText, DbTransaction != null, _parameters.Items.Select(p => (p.ParameterName, p.Value, p.DbType)).ToList(), CommandTimeout));
    }

    private sealed class RecordingParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull] public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull] public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType() { }
    }

    private sealed class RecordingParameters(bool drop) : DbParameterCollection
    {
        public List<DbParameter> Items { get; } = [];

        public override int Count => Items.Count;

        public override object SyncRoot => Items;

        public override int Add(object value)
        {
            if (!drop)
            {
                Items.Add((DbParameter)value);
            }

            return Items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var v in values)
            {
                Add(v);
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

    /// <summary>A one-column reader over the scripted rows that counts the rows it hands out.</summary>
    private sealed class RowReader(RecordingDbConnection owner) : DbDataReader
    {
        private int _index = -1;

        public override int Depth => 0;

        public override int FieldCount => 1;

        public override bool HasRows => owner.KeyRows.Count > 0;

        public override bool IsClosed => false;

        public override int RecordsAffected => 0;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(0);

        public override bool Read()
        {
            _index++;
            if (owner.FailAfterRows is { } limit && _index >= limit)
            {
                throw new InvalidOperationException("connection lost while reading");
            }

            if (_index >= owner.KeyRows.Count)
            {
                return false;
            }

            owner.ReadRow();
            return true;
        }

        public override bool IsDBNull(int ordinal) => owner.KeyRows[_index] is null;

        public override object GetValue(int ordinal) => owner.KeyRows[_index] ?? DBNull.Value;

        public override bool NextResult() => false;

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => string.Empty;

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override Type GetFieldType(int ordinal) => typeof(object);

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override string GetName(int ordinal) => "k";

        public override int GetOrdinal(string name) => 0;

        public override string GetString(int ordinal) => throw new NotSupportedException();

        public override int GetValues(object[] values) => throw new NotSupportedException();
    }
}

/// <summary>Hands out one prepared connection (or throws) and records how often it was asked.</summary>
internal sealed class FixedConnectionFactory(DbConnection? connection, Exception? failure = null) : ISqlConnectionFactory
{
    public int Opened { get; private set; }

    public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        Opened++;
        return failure is null ? Task.FromResult(connection!) : Task.FromException<DbConnection>(failure);
    }
}
