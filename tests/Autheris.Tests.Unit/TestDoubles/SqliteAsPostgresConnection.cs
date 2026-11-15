using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Autheris.Tests.Unit.TestDoubles;

/// <summary>
/// Wraps a <see cref="DbConnection"/> (such as an in-memory SQLite connection) so governed PostgreSQL
/// queries (positional <c>$1</c> parameters, <c>SET standard_conforming_strings</c>, <c>"public".</c>
/// schema qualification) can execute against it during unit tests.
/// </summary>
internal sealed class SqliteAsPostgresConnection(DbConnection inner, string? stubReaderJson = null) : DbConnection
{
    private readonly DbConnection _inner = inner;

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new WrappedTx(this, _inner.BeginTransaction(isolationLevel));

    protected override DbCommand CreateDbCommand() =>
        new WrappedCmd(this, _inner.CreateCommand(), stubReaderJson);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class WrappedTx(SqliteAsPostgresConnection owner, DbTransaction innerTx) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => innerTx.IsolationLevel;
        protected override DbConnection DbConnection => owner;
        public override void Commit() => innerTx.Commit();
        public override Task CommitAsync(CancellationToken cancellationToken = default) => innerTx.CommitAsync(cancellationToken);
        public override void Rollback() => innerTx.Rollback();
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => innerTx.RollbackAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerTx.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await innerTx.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class WrappedCmd(SqliteAsPostgresConnection owner, DbCommand innerCmd, string? stubReaderJson) : DbCommand
    {
        private WrappedTx? _tx;
        private readonly ParamCollection _params = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get => innerCmd.CommandTimeout; set => innerCmd.CommandTimeout = value; }
        public override CommandType CommandType { get => innerCmd.CommandType; set => innerCmd.CommandType = value; }
        public override bool DesignTimeVisible { get => innerCmd.DesignTimeVisible; set => innerCmd.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => innerCmd.UpdatedRowSource; set => innerCmd.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction
        {
            get => _tx;
            set => _tx = (WrappedTx?)value;
        }
        public override void Cancel() => innerCmd.Cancel();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new Param();

        public override int ExecuteNonQuery()
        {
            if (CommandText.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            SyncCommand();
            return innerCmd.ExecuteNonQuery();
        }

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            if (CommandText.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            SyncCommand();
            return await innerCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public override object? ExecuteScalar()
        {
            SyncCommand();
            return innerCmd.ExecuteScalar();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (stubReaderJson != null)
            {
                var table = new DataTable();
                table.Columns.Add("json", typeof(string));
                table.Rows.Add(stubReaderJson);
                return table.CreateDataReader();
            }

            SyncCommand();
            return innerCmd.ExecuteReader(behavior);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerCmd.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SyncCommand()
        {
            innerCmd.CommandText = CommandText.Replace("\"public\".", string.Empty, StringComparison.Ordinal);
            innerCmd.Parameters.Clear();
            for (int i = 0; i < _params.Count; i++)
            {
                var src = (Param)_params[i];
                var p = innerCmd.CreateParameter();
                p.ParameterName = string.IsNullOrEmpty(src.ParameterName) ? $"${i + 1}" : src.ParameterName;
                p.Value = src.Value ?? DBNull.Value;
                innerCmd.Parameters.Add(p);
            }
        }

        private sealed class ParamCollection : DbParameterCollection
        {
            private readonly List<DbParameter> _list = [];
            public override int Count => _list.Count;
            public override object SyncRoot => _list;
            public override int Add(object value) { _list.Add((DbParameter)value); return _list.Count - 1; }
            public override void AddRange(Array values) { foreach (DbParameter p in values) _list.Add(p); }
            public override void Clear() => _list.Clear();
            public override bool Contains(object value) => _list.Contains((DbParameter)value);
            public override bool Contains(string value) => IndexOf(value) >= 0;
            public override void CopyTo(Array array, int index) => ((ICollection)_list).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _list.GetEnumerator();
            protected override DbParameter GetParameter(int index) => _list[index];
            protected override DbParameter GetParameter(string parameterName) => _list[IndexOf(parameterName)];
            public override int IndexOf(object value) => _list.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) => _list.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
            public override void Insert(int index, object value) => _list.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _list.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _list.RemoveAt(index);
            public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _list.RemoveAt(i); }
            protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _list[i] = value; }
        }

        private sealed class Param : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
            public override bool IsNullable { get; set; }
            [AllowNull] public override string ParameterName { get; set; } = string.Empty;
            public override int Size { get; set; }
            [AllowNull] public override string SourceColumn { get; set; } = string.Empty;
            public override bool SourceColumnNullMapping { get; set; }
            public override object? Value { get; set; }
            public override void ResetDbType() { }
        }
    }
}
