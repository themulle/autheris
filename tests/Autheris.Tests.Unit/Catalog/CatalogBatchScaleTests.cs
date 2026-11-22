namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Scale")]
public sealed class CatalogBatchScaleTests
{
    [Fact]
    public async Task CatalogWriteScope_Scale_1k_10k_50k_OneBumpPerChunk_AndLinearTime()
    {
        var counts = new[] { 1_000, 10_000, 50_000 };
        var elapsedMs = new List<double>(counts.Length);

        foreach (var tableCount in counts)
        {
            var repo = new FakeCatalogRepository();
            var dirty = new CatalogDirtyFlag();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            var factory = new CatalogWriteScopeFactory(repo, dirty, timeProvider: time);

            var sw = Stopwatch.StartNew();
            await using (factory.Begin("scale-test"))
            {
                for (var i = 0; i < tableCount; i++)
                {
                    await repo.AdditiveWriteAsync();
                }
            }
            sw.Stop();

            repo.EpochIncrements.ShouldBe(tableCount);
            var expectedFlushes = (tableCount + CatalogWriteScopeSettings.ChunkSize - 1) / CatalogWriteScopeSettings.ChunkSize;
            repo.FlushCount.ShouldBe(expectedFlushes);
            elapsedMs.Add(Math.Max(sw.Elapsed.TotalMilliseconds, 0.1));
        }

        // 50k is 5x 10k; wall-clock time must stay linear (allow 15x ceiling for CI jitter).
        elapsedMs[2].ShouldBeLessThanOrEqualTo(Math.Max(elapsedMs[1] * 15.0, 2_000.0));
    }

    [Fact]
    public async Task UpsertTableMetadataBatchAsync_Scale_1k_10k_50k_PostgreSql_RoundTripsAtMostOne_AndLinearTime()
    {
        var scales = new[] { 1_000, 10_000, 50_000 };
        var elapsedMs = new List<double>(scales.Length);

        foreach (var tableCount in scales)
        {
            var harness = new StubDbHarness();
            var dialect = new StubGovernanceDialect(DatabaseDialect.PostgreSql, harness);
            var repo = new GovernanceRepository(dialect, options: Options.Create(new GatewayOptions()));
            var scopeFactory = new CatalogWriteScopeFactory(repo, new CatalogDirtyFlag());
            var batch = BuildBatch(tableCount, columnsPerTable: 5);

            var sw = Stopwatch.StartNew();
            await using (scopeFactory.Begin("scale-pg"))
            {
                var results = await repo.UpsertTableMetadataBatchAsync(batch, CancellationToken.None);
                results.Count.ShouldBe(tableCount);
            }
            sw.Stop();

            var avgRoundTripsPerTable = (double)harness.CommandCount / tableCount;
            avgRoundTripsPerTable.ShouldBeLessThanOrEqualTo(1.0);
            harness.MaxParametersPerCommand.ShouldBeLessThanOrEqualTo(
                DatabaseParameterBudgetProvider.Instance.GetMaxParameters(DatabaseDialect.PostgreSql));
            elapsedMs.Add(Math.Max(sw.Elapsed.TotalMilliseconds, 1.0));
        }

        // 50k is 5x 10k; verify linear growth (no O(n^2) degradation).
        elapsedMs[2].ShouldBeLessThanOrEqualTo(Math.Max(elapsedMs[1] * 12.0, 5_000.0));
    }

    [Fact]
    public async Task UpsertTableMetadataBatchAsync_Scale_1k_SqlServer_RespectsParameterBudget_AndRoundTripsAtMostOne()
    {
        const int tableCount = 1_000;
        var harness = new StubDbHarness();
        var dialect = new StubGovernanceDialect(DatabaseDialect.SqlServer, harness);
        var repo = new GovernanceRepository(dialect, options: Options.Create(new GatewayOptions()));
        var scopeFactory = new CatalogWriteScopeFactory(repo, new CatalogDirtyFlag());
        var batch = BuildBatch(tableCount, columnsPerTable: 5);

        await using (scopeFactory.Begin("scale-ss"))
        {
            var results = await repo.UpsertTableMetadataBatchAsync(batch, CancellationToken.None);
            results.Count.ShouldBe(tableCount);
        }

        var avgRoundTripsPerTable = (double)harness.CommandCount / tableCount;
        avgRoundTripsPerTable.ShouldBeLessThanOrEqualTo(1.0);
        harness.MaxParametersPerCommand.ShouldBeLessThanOrEqualTo(
            DatabaseParameterBudgetProvider.Instance.GetMaxParameters(DatabaseDialect.SqlServer));
    }

    private static List<TableMetadata> BuildBatch(int tableCount, int columnsPerTable)
    {
        var list = new List<TableMetadata>(tableCount);
        for (var i = 0; i < tableCount; i++)
        {
            var tableId = Guid.NewGuid();
            var cols = new List<TableColumn>(columnsPerTable);
            for (var c = 0; c < columnsPerTable; c++)
            {
                cols.Add(new TableColumn
                {
                    Id = Guid.NewGuid(),
                    TableId = tableId,
                    ColumnName = $"col_{c}",
                    DataType = c == 0 ? "integer" : "text"
                });
            }

            list.Add(new TableMetadata
            {
                Identifier = new TableIdentifier("scale", "dbo", $"tbl_{i:D5}"),
                Table = new Table
                {
                    Id = tableId,
                    SourceType = "PostgreSQL",
                    SourceName = "scale",
                    SchemaName = "dbo",
                    TableName = $"tbl_{i:D5}",
                    DisplayName = $"tbl_{i:D5}",
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = cols
            });
        }

        return list;
    }

    private sealed class StubGovernanceDialect(DatabaseDialect dialect, StubDbHarness harness) : IGovernanceSqlDialect
    {
        public string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns) => string.Empty;
        public string UpsertAccumulate(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> setOnInsertColumns, ReadOnlySpan<string> counterColumns) => string.Empty;
        public string InsertReturning(string column) => string.Empty;
        public string LockRowsForUpdate => string.Empty;
        public string UtcNow => "CURRENT_TIMESTAMP";
        public string JsonValue(string column, string path) => column;
        public string StringAggregate(string expr, string separator) => expr;
        public string LimitWithoutOrder(int n) => string.Empty;
        public bool IsUniqueViolation(DbException ex) => false;
        public bool IsTransient(DbException ex) => false;
        public IParameterBudgetProvider ParameterBudget { get; } = new DialectBudgetProvider(dialect);
        public DbConnection CreateConnection() => new StubDbConnection(harness);

        private sealed class DialectBudgetProvider(DatabaseDialect fixedDialect) : IParameterBudgetProvider
        {
            public int GetMaxParameters(DatabaseDialect d) => DatabaseParameterBudgetProvider.Instance.GetMaxParameters(fixedDialect);
            public int CalculateEffectiveChunkSize(int defaultChunkSize, int keyColumnCount, int contextParameterCount = 0, DatabaseDialect d = DatabaseDialect.SqlServer, int safetyBuffer = 50) =>
                DatabaseParameterBudgetProvider.Instance.CalculateEffectiveChunkSize(defaultChunkSize, keyColumnCount, contextParameterCount, fixedDialect, safetyBuffer);
        }
    }

    private sealed class StubDbHarness
    {
        private int _commandCount;
        private int _maxParams;

        public int CommandCount => Volatile.Read(ref _commandCount);
        public int MaxParametersPerCommand => Volatile.Read(ref _maxParams);

        public void RecordCommand(int paramCount)
        {
            Interlocked.Increment(ref _commandCount);
            int current;
            while (paramCount > (current = Volatile.Read(ref _maxParams)))
            {
                Interlocked.CompareExchange(ref _maxParams, paramCount, current);
            }
        }
    }

    private sealed class StubDbConnection(StubDbHarness harness) : DbConnection
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
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new StubDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new StubDbCommand(harness, this);
    }

    private sealed class StubDbTransaction(DbConnection conn) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => conn;
        public override void Commit() { }
        public override Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override void Rollback() { }
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubDbCommand(StubDbHarness harness, DbConnection conn) : DbCommand
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

        public override int ExecuteNonQuery()
        {
            harness.RecordCommand(_parameters.Count);
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            harness.RecordCommand(_parameters.Count);
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar()
        {
            harness.RecordCommand(_parameters.Count);
            return 1;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            harness.RecordCommand(_parameters.Count);
            return Task.FromResult<object?>(1);
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            harness.RecordCommand(_parameters.Count);
            return CreateReader(CommandText);
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            harness.RecordCommand(_parameters.Count);
            return Task.FromResult(CreateReader(CommandText));
        }

        private static DbDataReader CreateReader(string sql)
        {
            if (sql.Contains("catalog_state", StringComparison.OrdinalIgnoreCase))
            {
                return new SingleVersionReader();
            }

            return EmptyReader.Instance;
        }
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

    private sealed class SingleVersionReader : DbDataReader
    {
        private int _row = -1;
        public override int FieldCount => 2;
        public override int RecordsAffected => 1;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool Read() => ++_row == 0;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(++_row == 0);
        public override bool NextResult() => false;
        public override int GetOrdinal(string name) => name == "content_version" ? 1 : 0;
        public override string GetName(int ordinal) => ordinal == 0 ? "structure_version" : "content_version";
        public override string GetDataTypeName(int ordinal) => "bigint";
        public override Type GetFieldType(int ordinal) => typeof(long);
        public override object GetValue(int ordinal) => ordinal == 0 ? 1L : 0L;
        public override int GetValues(object[] values) { values[0] = 1L; values[1] = 0L; return 2; }
        public override bool IsDBNull(int ordinal) => false;
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 1;
        public override long GetInt64(int ordinal) => ordinal == 0 ? 1L : 0L;
        public override float GetFloat(int ordinal) => 0;
        public override double GetDouble(int ordinal) => 0;
        public override decimal GetDecimal(int ordinal) => 0;
        public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
        public override string GetString(int ordinal) => "1";
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
