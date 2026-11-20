namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ProcedureRowScopeKeyClosureTests
{
    private static TableMetadata OrdersTable(DatabaseDialect dialect = DatabaseDialect.SqlServer) => new()
    {
        Identifier = new TableIdentifier("erp", "sales", "orders"),
        Table = new Table
        {
            SourceName = "erp",
            SchemaName = "sales",
            TableName = "orders",
            SourceType = dialect.ToString(),
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "order_id" },
            new TableColumn { ColumnName = "tenant_id" },
            new TableColumn { ColumnName = "status" }
        ]
    };

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(
        DatabaseDialect.SqlServer,
        "SELECT [order_id] FROM (SELECT [order_id], CASE WHEN [tenant_id] = @rs_tenant AND ([autheris_target].[status] = 'open') THEN 1 ELSE 0 END AS [autheris_rs_ok] FROM [sales].[orders] AS [autheris_target] WHERE (([order_id] = @rs_0_0) OR ([order_id] = @rs_1_0))) AS [autheris_rs] GROUP BY [order_id] HAVING COUNT(*) = SUM([autheris_rs_ok])")]
    [InlineData(
        DatabaseDialect.PostgreSql,
        "SELECT \"order_id\" FROM (SELECT \"order_id\", CASE WHEN \"tenant_id\" = @rs_tenant AND (\"autheris_target\".\"status\" = 'open') THEN 1 ELSE 0 END AS \"autheris_rs_ok\" FROM \"sales\".\"orders\" AS \"autheris_target\" WHERE ((\"order_id\" = @rs_0_0) OR (\"order_id\" = @rs_1_0))) AS \"autheris_rs\" GROUP BY \"order_id\" HAVING COUNT(*) = SUM(\"autheris_rs_ok\")")]
    public void BuildKeyQuery_GroupsByKey_HavingAllRowsPassFilterAndTenant(DatabaseDialect dialect, string expectedSql)
    {
        string filter = dialect switch
        {
            DatabaseDialect.SqlServer => "[autheris_target].[status] = 'open'",
            _ => "\"autheris_target\".\"status\" = 'open'"
        };

        string sql = SqlProcedureRowScopeResolver.BuildKeyQuery(
            dialect,
            OrdersTable(dialect),
            ["order_id"],
            "tenant_id",
            filter,
            2);

        sql.ShouldBe(expectedSql);
        sql.ShouldNotContain("sys.indexes");
        sql.ShouldNotContain("pg_index");
        sql.ShouldNotContain("pragma_");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.PostgreSql)]
    public void BuildKeyQuery_SubqueryFilter_IsNotInsideAggregate(DatabaseDialect dialect)
    {
        string filter = dialect switch
        {
            DatabaseDialect.SqlServer => "EXISTS (SELECT 1 FROM [dbo].[client] AS [c] WHERE [c].[client_id] = [autheris_target].[client_id] AND [c].[region] = N'DE')",
            _ => "EXISTS (SELECT 1 FROM \"dbo\".\"client\" AS \"c\" WHERE \"c\".\"client_id\" = \"autheris_target\".\"client_id\" AND \"c\".\"region\" = 'DE')"
        };

        string sql = SqlProcedureRowScopeResolver.BuildKeyQuery(
            dialect,
            OrdersTable(dialect),
            ["order_id"],
            "tenant_id",
            filter,
            1);

        int sumStart = sql.IndexOf("SUM(", StringComparison.Ordinal);
        sumStart.ShouldBeGreaterThan(0);
        int argStart = sumStart + 4;
        int depth = 1;
        int i = argStart;
        while (i < sql.Length && depth > 0)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
            i++;
        }

        depth.ShouldBe(0);
        string sumArg = sql.Substring(argStart, i - 1 - argStart);
        sumArg.ShouldBe(dialect.QuoteIdentifier(SqlProcedureRowScopeResolver.RowScopeFlagColumn));
        SqlProcedureRowScopeResolver.RowScopeFlagColumn.ShouldBe("autheris_rs_ok");
        SqlProcedureRowScopeResolver.RowScopeDerivedAlias.ShouldBe("autheris_rs");
        sumArg.ShouldNotContain("SELECT", Case.Insensitive);
        sumArg.ShouldNotContain("EXISTS", Case.Insensitive);
    }

    [Fact]
    public async Task GetAllowedKeysAsync_ExistsFilterAndParameters_PreservesParameterOrder()
    {
        var conn = new RecordingDbConnection([["ord-2"]]);
        var resolver = new SqlProcedureRowScopeResolver(
            new SingleConnectionFactory(conn),
            Options.Create(new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["erp"] = new() { Provider = "SqlServer", ConnectionString = "Server=localhost;Database=erp" }
                    }
                }
            }));

        var table = OrdersTable();
        var definition = ProcedureDefinitionParser.ParseYaml(
            "name: p\nprocedure: api.usp_P\nresult_table: sales.orders\ntables: [sales.orders]\noutputs: [order_id]\nrow_scope_key: order_id\n",
            "erp",
            allowRlsNone: false,
            maxTimeoutSeconds: 60) with
        {
            DataSource = "erp"
        };

        var decision = TableAccessDecision.Allowed(
            table.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: "EXISTS (SELECT 1 FROM dbo.client r WHERE r.client_id = autheris_target.client_id AND r.region = @p_region)",
            rowFilterParameters: new Dictionary<string, object?> { ["@p_region"] = "DE" },
            hasUnconstrainedColumnAllow: true);

        var allowed = await resolver.GetAllowedKeysAsync(
            definition,
            table,
            decision,
            ["order_id"],
            [["ord-1"], ["ord-2"]],
            new ProcedureSecurityContext("tenant-a", "S-1-5-21-1", null),
            CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["ord-2"])! });
        conn.BoundParameterNames.ShouldBe(["@rs_tenant", "@p_region", "@rs_0_0", "@rs_1_0"]);
        conn.BoundParameters["@p_region"].ShouldBe("DE");
    }

    [Fact]
    public async Task UniqueKey_SameResultAsBefore()
    {
        var conn = new RecordingDbConnection([["ord-2"]]);
        var resolver = new SqlProcedureRowScopeResolver(
            new SingleConnectionFactory(conn),
            Options.Create(new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>
                    {
                        ["erp"] = new() { Provider = "SqlServer", ConnectionString = "Server=localhost;Database=erp" }
                    }
                }
            }));

        var table = OrdersTable();
        var definition = ProcedureDefinitionParser.ParseYaml(
            "name: p\nprocedure: api.usp_P\nresult_table: sales.orders\ntables: [sales.orders]\noutputs: [order_id]\nrow_scope_key: order_id\n",
            "erp",
            allowRlsNone: false,
            maxTimeoutSeconds: 60) with
        {
            DataSource = "erp"
        };

        var decision = TableAccessDecision.Allowed(
            table.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: "[autheris_target].[status] = 'open'",
            hasUnconstrainedColumnAllow: true);

        var allowed = await resolver.GetAllowedKeysAsync(
            definition,
            table,
            decision,
            ["order_id"],
            [["ord-1"], ["ord-2"]],
            new ProcedureSecurityContext("tenant-a", "S-1-5-21-1", null),
            CancellationToken.None);

        allowed.ShouldBe(new[] { RowScopeKeys.Normalize(["ord-2"])! });
        conn.ExecutedQueries.Count.ShouldBe(1);
        conn.ExecutedQueries[0].ShouldContain("CASE WHEN [tenant_id] = @rs_tenant AND ([autheris_target].[status] = 'open') THEN 1 ELSE 0 END AS [autheris_rs_ok]");
        conn.ExecutedQueries[0].ShouldContain("GROUP BY [order_id]");
        conn.ExecutedQueries[0].ShouldContain("HAVING COUNT(*) = SUM([autheris_rs_ok])");
        conn.ExecutedQueries.ShouldAllBe(q => !q.Contains("sys.indexes", StringComparison.OrdinalIgnoreCase));
        conn.BoundParameters["@rs_tenant"].ShouldBe("tenant-a");
        conn.BoundParameters["@rs_0_0"].ShouldBe("ord-1");
        conn.BoundParameters["@rs_1_0"].ShouldBe("ord-2");
    }

    private sealed class SingleConnectionFactory(DbConnection connection) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
            Task.FromResult(connection);
    }

    private sealed class RecordingDbConnection(IReadOnlyList<object?[]> rows) : DbConnection
    {
        public List<string> ExecutedQueries { get; } = [];
        public List<string> BoundParameterNames { get; } = [];
        public Dictionary<string, object?> BoundParameters { get; } = new(StringComparer.Ordinal);

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "erp";
        public override string DataSource => "localhost";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this, rows);
    }

    private sealed class RecordingDbCommand(RecordingDbConnection owner, IReadOnlyList<object?[]> rows) : DbCommand
    {
        private readonly RecordingParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new RecordingParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                owner.ExecutedQueries.Add(CommandText);
                foreach (RecordingParameter p in _parameters)
                {
                    owner.BoundParameterNames.Add(p.ParameterName);
                    owner.BoundParameters[p.ParameterName] = p.Value;
                }

                return new RecordingDataReader(rows);
            }

            return new RecordingDataReader([]);
        }
    }

    private sealed class RecordingParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
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

    private sealed class RecordingParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
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
        public override void RemoveAt(string parameterName) { int i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class RecordingDataReader(IReadOnlyList<object?[]> rows) : DbDataReader
    {
        private int _index = -1;
        public override int FieldCount => rows.Count > 0 ? rows[0].Length : 1;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;
        public override object GetValue(int ordinal) => rows[_index][ordinal]!;
        public override string GetString(int ordinal) => (string)rows[_index][ordinal]!;
        public override int GetInt32(int ordinal) => Convert.ToInt32(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override byte GetByte(int ordinal) => Convert.ToByte(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override char GetChar(int ordinal) => Convert.ToChar(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override DateTime GetDateTime(int ordinal) => (DateTime)rows[_index][ordinal]!;
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override float GetFloat(int ordinal) => Convert.ToSingle(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override Guid GetGuid(int ordinal) => (Guid)rows[_index][ordinal]!;
        public override short GetInt16(int ordinal) => Convert.ToInt16(rows[_index][ordinal], System.Globalization.CultureInfo.InvariantCulture);
        public override string GetDataTypeName(int ordinal) => "text";
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override string GetName(int ordinal) => $"col{ordinal}";
        public override int GetOrdinal(string name) => 0;
        public override int GetValues(object[] values) { rows[_index].CopyTo(values, 0); return rows[_index].Length; }
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override IEnumerator GetEnumerator() => rows.GetEnumerator();
        public override bool NextResult() => false;
    }
}
