using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>
/// Scripted SQL Server catalog connection for <c>StoredProcedureCatalogValidator</c> tests: a stored procedure 'P' with EXECUTE
/// permission, a harmless module and the given parameter names. Every other query returns an empty result.
/// </summary>
internal sealed class ScriptedCatalogConnection(params string[] parameterNames) : DbConnection
{
    [AllowNull] public override string ConnectionString { get => string.Empty; set { } }

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() { }

    public override void Open() { }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new Cmd(parameterNames);

    private sealed class Cmd(string[] parameterNames) : DbCommand
    {
        private readonly Params _parameters = new();

        [AllowNull] public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar() => CommandText.Contains("HAS_PERMS_BY_NAME", StringComparison.Ordinal) ? 1 : null;

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new Param();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            if (CommandText.Contains("FROM sys.objects", StringComparison.Ordinal))
            {
                table.Columns.Add("object_id", typeof(int));
                table.Columns.Add("type", typeof(string));
                table.Rows.Add(1, "P");
            }
            else if (CommandText.Contains("FROM sys.sql_modules", StringComparison.Ordinal))
            {
                table.Columns.Add("definition", typeof(string));
                table.Columns.Add("execute_as_principal_id", typeof(int));
                table.Rows.Add("SELECT 1", DBNull.Value);
            }
            else if (CommandText.Contains("FROM sys.parameters", StringComparison.Ordinal))
            {
                table.Columns.Add("name", typeof(string));
                table.Columns.Add("type", typeof(string));
                table.Columns.Add("max_length", typeof(int));
                table.Columns.Add("has_default_value", typeof(bool));
                table.Columns.Add("is_output", typeof(bool));
                foreach (var name in parameterNames)
                {
                    table.Rows.Add(name, "int", 4, false, false);
                }
            }
            else
            {
                table.Columns.Add("c", typeof(string));
            }

            return new DataTableReader(table);
        }
    }

    private sealed class Param : DbParameter
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

    private sealed class Params : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => _items;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var v in values)
            {
                Add(v);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => _items.Exists(p => p.ParameterName == value);

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }
}
