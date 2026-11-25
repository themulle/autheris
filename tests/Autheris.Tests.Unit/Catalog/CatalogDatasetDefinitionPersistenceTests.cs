namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogDatasetDefinitionPersistenceTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public async Task PutDatasetDefinitionHandler_WithRealGovernanceRepository_OverwritesPoisonedSourceTypeAndCommits(
        DatabaseDialect governanceEngine)
    {
        var tableGuid = Guid.NewGuid();
        var colGuid = Guid.NewGuid();
        var tableId = new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date");
        var harness = new InMemoryCatalogDbHarness(
            tableGuid,
            colGuid,
            initialSourceType: "kostentransparenz_dbx",
            sourceName: "kostentransparenz_dbx",
            schemaName: "controlling",
            tableName: "dim_date",
            columnName: "Year",
            columnDataType: "int");

        IGovernanceSqlDialect dialect = governanceEngine == DatabaseDialect.PostgreSql
            ? new PostgreSqlGovernanceDialect(() => new StubDbConnection(harness))
            : new SqlServerGovernanceDialect(() => new StubDbConnection(harness));

        var repo = new GovernanceRepository(dialect, options: Options.Create(new GatewayOptions()));
        var audit = new NoopAuditWriter();
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kostentransparenz_dbx"] = new DataSourceConnectionOptions { Provider = "Databricks" }
                }
            }
        });

        const string payloadJson = """
            {
              "schemaVersion": 1,
              "dataSource": "kostentransparenz_dbx",
              "kind": "table",
              "columns": [
                { "name": "Year", "nativeType": "int" }
              ]
            }
            """;
        var request = JsonSerializer.Deserialize<CatalogDatasetDefinitionRequest>(payloadJson, WebJsonOptions);
        request.ShouldNotBeNull();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin")],
                "TestAuth"))
        };

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "kostentransparenz_dbx.controlling.dim_date",
            request,
            repo,
            audit,
            gatewayOptions,
            httpContext,
            CancellationToken.None);

        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        status.ShouldBe(StatusCodes.Status200OK);

        var upsertTableCmd = harness.Commands.Single(c =>
            c.Sql.StartsWith("INSERT INTO TABLES ", StringComparison.Ordinal) ||
            c.Sql.StartsWith("MERGE dbo.TABLES ", StringComparison.Ordinal));
        upsertTableCmd.Params["@sourceType0"].ShouldBe("Databricks");

        harness.CommittedTransactions.ShouldBe(1);
        harness.RolledBackTransactions.ShouldBe(0);

        var loadedCached = await repo.GetTableMetadataAsync(tableId, CancellationToken.None);
        loadedCached.ShouldNotBeNull();
        loadedCached.Table.SourceType.ShouldBe("Databricks");
        loadedCached.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);

        repo.InvalidateMetadataCacheForTesting();
        var reloaded = await repo.GetTableMetadataAsync(tableId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        reloaded.Table.SourceType.ShouldBe("Databricks");
        reloaded.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);
        reloaded.Columns.Select(c => c.ColumnName).ShouldBe(["Year"]);
    }

    private sealed class NoopAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record RecordedCommand(string Sql, IReadOnlyDictionary<string, object?> Params);

    private sealed class InMemoryCatalogDbHarness
    {
        public Guid TableId { get; }
        public Guid ColumnId { get; }
        public string SourceType { get; private set; }
        public string SourceName { get; }
        public string SchemaName { get; }
        public string TableName { get; }
        public string ColumnName { get; }
        public string ColumnDataType { get; }
        public long Epoch { get; private set; } = 1L;
        public long StructureVersion { get; private set; } = 1L;
        public int CommittedTransactions { get; private set; }
        public int RolledBackTransactions { get; private set; }
        public List<RecordedCommand> Commands { get; } = [];

        public InMemoryCatalogDbHarness(
            Guid tableId,
            Guid columnId,
            string initialSourceType,
            string sourceName,
            string schemaName,
            string tableName,
            string columnName,
            string columnDataType)
        {
            TableId = tableId;
            ColumnId = columnId;
            SourceType = initialSourceType;
            SourceName = sourceName;
            SchemaName = schemaName;
            TableName = tableName;
            ColumnName = columnName;
            ColumnDataType = columnDataType;
        }

        public void RecordCommit() => CommittedTransactions++;
        public void RecordRollback() => RolledBackTransactions++;

        public int ExecuteNonQuery(string sql, StubParameterCollection parameters)
        {
            var dict = SnapshotParams(parameters);
            Commands.Add(new RecordedCommand(sql, dict));

            if (sql.StartsWith("INSERT INTO TABLES ", StringComparison.Ordinal) ||
                sql.StartsWith("MERGE dbo.TABLES ", StringComparison.Ordinal))
            {
                if (dict.TryGetValue("@sourceType0", out var st) && st is string sourceTypeStr)
                {
                    SourceType = sourceTypeStr;
                }
            }
            else if (sql.Contains("POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase))
            {
                Epoch++;
            }

            return 1;
        }

        public object? ExecuteScalar(string sql, StubParameterCollection parameters)
        {
            var dict = SnapshotParams(parameters);
            Commands.Add(new RecordedCommand(sql, dict));

            if (sql.Contains("SESSIONPROPERTY('QUOTED_IDENTIFIER')", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (sql.Contains("to_regclass", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains("OBJECT_ID", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (sql.Contains("POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase))
            {
                return Epoch;
            }

            return null;
        }

        public DbDataReader ExecuteReader(string sql, StubParameterCollection parameters)
        {
            var dict = SnapshotParams(parameters);
            Commands.Add(new RecordedCommand(sql, dict));

            if (sql.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase))
            {
                return new TableEntityReader(this);
            }

            if (sql.Contains("FROM TABLE_COLUMNS", StringComparison.OrdinalIgnoreCase))
            {
                if (sql.Contains("MAX(ordinal)", StringComparison.OrdinalIgnoreCase))
                {
                    return EmptyReader.Instance;
                }

                return new TableColumnsReader(this);
            }

            if (sql.Contains("POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase))
            {
                return new EpochListReader(TableId, Epoch);
            }

            if (sql.Contains("catalog_state", StringComparison.OrdinalIgnoreCase))
            {
                StructureVersion++;
                return new CatalogVersionReader(StructureVersion, 0L);
            }

            return EmptyReader.Instance;
        }

        private static Dictionary<string, object?> SnapshotParams(StubParameterCollection parameters)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DbParameter p in parameters)
            {
                dict[p.ParameterName] = p.Value;
            }
            return dict;
        }
    }

    private sealed class StubDbConnection(InMemoryCatalogDbHarness harness) : DbConnection
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
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new StubDbTransaction(harness, this);
        protected override DbCommand CreateDbCommand() => new StubDbCommand(harness, this);
    }

    private sealed class StubDbTransaction(InMemoryCatalogDbHarness harness, DbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() => harness.RecordCommit();
        public override Task CommitAsync(CancellationToken cancellationToken = default) { harness.RecordCommit(); return Task.CompletedTask; }
        public override void Rollback() => harness.RecordRollback();
        public override Task RollbackAsync(CancellationToken cancellationToken = default) { harness.RecordRollback(); return Task.CompletedTask; }
    }

    private sealed class StubDbCommand(InMemoryCatalogDbHarness harness, DbConnection conn) : DbCommand
    {
        private readonly StubParameterCollection _parameters = new();
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
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new StubDbParameter();
        public override int ExecuteNonQuery() => harness.ExecuteNonQuery(CommandText, _parameters);
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(harness.ExecuteNonQuery(CommandText, _parameters));
        public override object? ExecuteScalar() => harness.ExecuteScalar(CommandText, _parameters);
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult(harness.ExecuteScalar(CommandText, _parameters));
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => harness.ExecuteReader(CommandText, _parameters);
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => Task.FromResult(harness.ExecuteReader(CommandText, _parameters));
    }

    private sealed class TableEntityReader(InMemoryCatalogDbHarness harness) : SingleRowReaderBase(20)
    {
        protected override object GetRowValue(int ordinal) => ordinal switch
        {
            0 => harness.TableId.ToString(),
            1 => harness.SourceType,
            2 => harness.SourceName,
            3 => harness.SchemaName,
            4 => harness.TableName,
            5 => harness.TableName,
            6 => 1,
            7 => (int)DataSourceType.Sql,
            16 => false,
            18 => "table",
            _ => DBNull.Value
        };
    }

    private sealed class TableColumnsReader(InMemoryCatalogDbHarness harness) : SingleRowReaderBase(16)
    {
        protected override object GetRowValue(int ordinal) => ordinal switch
        {
            0 => harness.ColumnId.ToString(),
            1 => harness.TableId.ToString(),
            2 => harness.ColumnName,
            3 => harness.ColumnDataType,
            _ => DBNull.Value
        };
    }

    private sealed class EpochListReader(Guid tableId, long epoch) : SingleRowReaderBase(2)
    {
        protected override object GetRowValue(int ordinal) => ordinal == 0 ? tableId.ToString() : epoch;
    }

    private sealed class CatalogVersionReader(long structureVersion, long contentVersion) : SingleRowReaderBase(2)
    {
        protected override object GetRowValue(int ordinal) => ordinal == 0 ? structureVersion : contentVersion;
    }

    private abstract class SingleRowReaderBase(int fieldCount) : DbDataReader
    {
        private int _row = -1;
        public override int FieldCount => fieldCount;
        public override int RecordsAffected => 1;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool Read() => ++_row == 0;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(++_row == 0);
        public override bool NextResult() => false;
        public override int GetOrdinal(string name) => -1;
        public override string GetName(int ordinal) => string.Empty;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override object GetValue(int ordinal) => GetRowValue(ordinal);
        protected abstract object GetRowValue(int ordinal);
        public override int GetValues(object[] values) { for (var i = 0; i < fieldCount; i++) values[i] = GetValue(i); return fieldCount; }
        public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull or null;
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    private sealed class EmptyReader : DbDataReader
    {
        public static readonly EmptyReader Instance = new();
        public override int FieldCount => 0;
        public override int RecordsAffected => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override object this[int ordinal] => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public override object this[string name] => throw new ArgumentOutOfRangeException(nameof(name));
        public override bool Read() => false;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override bool NextResult() => false;
        public override int GetOrdinal(string name) => -1;
        public override string GetName(int ordinal) => string.Empty;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override float GetFloat(int ordinal) => 0;
        public override double GetDouble(int ordinal) => 0;
        public override decimal GetDecimal(int ordinal) => 0;
        public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
        public override string GetString(int ordinal) => string.Empty;
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    private sealed class StubDbParameter : DbParameter
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

    private sealed class StubParameterCollection : DbParameterCollection
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
