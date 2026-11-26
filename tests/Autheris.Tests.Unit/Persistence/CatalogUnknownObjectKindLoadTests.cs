using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.Migrations;
using Autheris.Tests.Unit.Sql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

[Trait("Category", "Security")]
public sealed class CatalogUnknownObjectKindLoadTests
{
    [Fact]
    public async Task LoadActiveTables_SkipsUnknownKindRow_KeepsValidRows_LogsOneWarning()
    {
        var logger = new ListCapturingLogger<GovernanceRepository>();
        var repo = new GovernanceRepository(new PostgreSqlGovernanceDialect("Host=unused"), logger: logger);
        using var cmd = new StubCommand(() => ThreeRows());

        var tables = await repo.LoadActiveTablesAsync(cmd, CancellationToken.None);

        tables.Count.ShouldBe(2);
        tables.Select(t => t.Table.ObjectKind).ShouldBe([DiscoveryObjectKind.Table, DiscoveryObjectKind.View]);
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].ShouldContain("bogus");
        logger.Entries.ShouldNotContain(e => e.Contains("Catalog notice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetTableMetadata_UnknownKindRow_ReturnsNull_AndDoesNotCache()
    {
        var logger = new ListCapturingLogger<GovernanceRepository>();
        var queries = 0;
        var dialect = new PostgreSqlGovernanceDialect(() => new StubConnection(() =>
        {
            queries++;
            return TableObjectKindMigration0030Tests.RowWith("bogus").CreateDataReader();
        }));
        var repo = new GovernanceRepository(dialect, logger: logger);
        var id = new TableIdentifier("d", "s", "t");

        (await repo.GetTableMetadataAsync(id, CancellationToken.None)).ShouldBeNull();
        (await repo.GetTableMetadataAsync(id, CancellationToken.None)).ShouldBeNull();

        queries.ShouldBe(2);
        logger.Entries.Count.ShouldBe(2);
    }

    private static DbDataReader ThreeRows()
    {
        var table = TableObjectKindMigration0030Tests.RowWith("table");
        foreach (var (kind, name) in new[] { ("bogus", "t2"), ("view", "t3") })
        {
            var row = table.NewRow();
            foreach (DataColumn c in table.Columns) row[c] = table.Rows[0][c];
            row["id"] = Guid.NewGuid().ToString();
            row["object_kind"] = kind;
            row["table_name"] = name;
            table.Rows.Add(row);
        }

        return table.CreateDataReader();
    }

    private sealed class StubConnection(Func<DbDataReader> reader) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken) { _state = ConnectionState.Open; return Task.CompletedTask; }
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new StubCommand(reader);
    }

    private sealed class StubCommand(Func<DbDataReader> reader) : DbCommand
    {
        private readonly StubParameters _parameters = new();
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        protected override DbParameter CreateDbParameter() => new StubParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => reader();
    }

    private sealed class StubParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class StubParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
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
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _items[i] = value; }
    }
}
