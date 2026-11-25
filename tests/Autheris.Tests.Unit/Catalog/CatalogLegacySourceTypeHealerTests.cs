namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogLegacySourceTypeHealerTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task HealConfiguredSqlTablesAsync_HealsLegacyConnectionsAndWebSqlMappings_InOneWriteScopedBatch()
    {
        var dbxId = new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date");
        var oraId = new TableIdentifier("accon_oracle", "fin", "invoices");
        var dbxColId = Guid.NewGuid();
        var repo = new FakeCatalogMetadataRepository();
        repo.Seed(new TableMetadata
        {
            Identifier = dbxId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "kostentransparenz_dbx",
                SchemaName = "controlling",
                TableName = "dim_date",
                SourceType = "kostentransparenz_dbx",
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { Id = dbxColId, ColumnName = "Year", DataType = "int" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["Year"] = new MaskingRule { TableColumnId = dbxColId, RuleType = "REDACT", Replacement = "***" }
            },
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Year"] = [new ColumnClassification { TableColumnId = dbxColId.ToString(), Code = "PII", Source = "MANUAL", Scope = "*" }]
            }
        });
        repo.Seed(new TableMetadata
        {
            Identifier = oraId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "accon_oracle",
                SchemaName = "fin",
                TableName = "invoices",
                SourceType = "accon_oracle",
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "invoice_id", DataType = "number" }]
        });

        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kostentransparenz_dbx"] = new() { Provider = "Databricks" },
                    ["oracle_prod"] = new() { Provider = "Oracle" }
                }
            },
            WebSql = new WebSqlOptions
            {
                DataSourceMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["accon_oracle"] = "oracle_prod"
                }
            }
        };

        var scopeFactory = new RecordingWriteScopeFactory();
        var logger = new RecordingLogger();

        var healed = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(
            repo, options, scopeFactory, logger, CancellationToken.None);

        healed.ShouldBe(2);
        repo.BatchUpsertCalls.ShouldBe(1);
        repo.LastBatchCount.ShouldBe(2);
        scopeFactory.BegunWriters.ShouldBe(["legacy-source-type-heal"]);
        scopeFactory.DisposedCount.ShouldBe(1);

        var healedDbx = repo.GetStored(dbxId);
        healedDbx.Table.SourceType.ShouldBe("Databricks");
        healedDbx.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);
        healedDbx.ColumnMaskingRules.ShouldContainKey("Year");
        healedDbx.ColumnClassifications.ShouldContainKey("Year");

        var healedOra = repo.GetStored(oraId);
        healedOra.Table.SourceType.ShouldBe("Oracle");
        healedOra.Table.Dialect.ShouldBe(DatabaseDialect.Oracle);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HealConfiguredSqlTablesAsync_UnconfiguredOrUnsupportedProviders_LeaveTablesUntouched_FailClosed()
    {
        var unconfiguredId = new TableIdentifier("unconfigured_conn", "public", "orders");
        var unsupportedId = new TableIdentifier("unsupported_conn", "public", "events");
        var healthyId = new TableIdentifier("valid_pg", "public", "users");

        var repo = new FakeCatalogMetadataRepository();
        repo.Seed(BuildSqlTable(unconfiguredId, "unconfigured_conn"));
        repo.Seed(BuildSqlTable(unsupportedId, "unsupported_conn"));
        repo.Seed(BuildSqlTable(healthyId, "PostgreSql"));

        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unsupported_conn"] = new() { Provider = "sqlite" },
                    ["valid_pg"] = new() { Provider = "PostgreSql" }
                }
            }
        };

        var healed = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(
            repo, options, ct: CancellationToken.None);

        healed.ShouldBe(0);
        repo.BatchUpsertCalls.ShouldBe(0);
        repo.GetMetadataCalls.ShouldBe(0);
        repo.GetStored(unconfiguredId).Table.SourceType.ShouldBe("unconfigured_conn");
        repo.GetStored(unsupportedId).Table.SourceType.ShouldBe("unsupported_conn");
        repo.GetStored(healthyId).Table.SourceType.ShouldBe("PostgreSql");
    }

    [Fact]
    public async Task HealConfiguredSqlTablesAsync_WhenAllSqlTablesHaveValidDialects_PerformsZeroUpsertsAndZeroMetadataReads()
    {
        var repo = new FakeCatalogMetadataRepository();
        repo.Seed(BuildSqlTable(new TableIdentifier("pg", "public", "a"), "PostgreSql"));
        repo.Seed(BuildSqlTable(new TableIdentifier("ss", "dbo", "b"), "SqlServer"));
        repo.Seed(BuildSqlTable(new TableIdentifier("dbx", "analytics", "c"), "Databricks"));
        repo.Seed(BuildSqlTable(new TableIdentifier("ora", "fin", "d"), "Oracle"));

        var scopeFactory = new RecordingWriteScopeFactory();
        var options = new GatewayOptions();

        var healed = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(
            repo, options, scopeFactory, NullLogger.Instance, CancellationToken.None);

        healed.ShouldBe(0);
        repo.GetMetadataCalls.ShouldBe(0);
        repo.BatchUpsertCalls.ShouldBe(0);
        scopeFactory.BegunWriters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HealConfiguredSqlTablesAsync_NonSqlTables_AreNeverTouched()
    {
        var httpId = new TableIdentifier("rest_api", "v1", "customers");
        var repo = new FakeCatalogMetadataRepository();
        repo.Seed(new TableMetadata
        {
            Identifier = httpId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "rest_api",
                SchemaName = "v1",
                TableName = "customers",
                SourceType = "rest_api",
                IsActive = true,
                DataSourceType = DataSourceType.HttpDeclarative
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "string" }]
        });

        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rest_api"] = new() { Provider = "PostgreSql" }
                }
            }
        };

        var healed = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(
            repo, options, ct: CancellationToken.None);

        healed.ShouldBe(0);
        repo.GetMetadataCalls.ShouldBe(0);
        repo.BatchUpsertCalls.ShouldBe(0);
        repo.GetStored(httpId).Table.SourceType.ShouldBe("rest_api");
    }

    [Fact]
    public async Task HealConfiguredSqlTablesAsync_InactiveAndNullArgumentsAndCancellation_HandledCleanly()
    {
        var repo = new FakeCatalogMetadataRepository();
        var inactiveId = new TableIdentifier("kostentransparenz_dbx", "controlling", "inactive_tbl");
        repo.Seed(BuildSqlTable(inactiveId, "kostentransparenz_dbx", isActive: false));

        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kostentransparenz_dbx"] = new() { Provider = "Databricks" }
                }
            }
        };

        await Should.ThrowAsync<ArgumentNullException>(() =>
            CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(null!, options));
        await Should.ThrowAsync<ArgumentNullException>(() =>
            CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(repo, null!));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(repo, options, ct: cts.Token));

        var healed = await CatalogLegacySourceTypeHealer.HealConfiguredSqlTablesAsync(repo, options);
        healed.ShouldBe(0);
        repo.BatchUpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task VirtualSystemTablesHostedService_StartAsync_HealsLegacyTablesAndRefreshesSnapshotWhenHealed()
    {
        var legacyId = new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date");
        var repo = new FakeCatalogMetadataRepository();
        repo.Seed(BuildSqlTable(legacyId, "kostentransparenz_dbx"));

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kostentransparenz_dbx"] = new() { Provider = "Databricks" }
                }
            }
        });
        var snapshotProvider = new RecordingSnapshotProvider();
        var scopeFactory = new RecordingWriteScopeFactory();

        var services = new ServiceCollection();
        services.AddSingleton<ITableMetadataRepository>(repo);
        services.AddSingleton<IOptions<GatewayOptions>>(options);
        services.AddSingleton<ICatalogSnapshotProvider>(snapshotProvider);
        services.AddSingleton<ICatalogWriteScopeFactory>(scopeFactory);
        using var sp = services.BuildServiceProvider();

        var hostedService = new VirtualSystemTablesHostedService(sp, NullLogger<VirtualSystemTablesHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        repo.GetStored(legacyId).Table.SourceType.ShouldBe("Databricks");
        snapshotProvider.RefreshCalls.ShouldBe(1);

        await hostedService.StartAsync(CancellationToken.None);
        snapshotProvider.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GovernanceRepository_GetUnparseableSqlSourceTypesAsync_UsesLightweightQueryAndSkipsInvalidIdentifiers()
    {
        var rows = new List<(string SourceType, string SourceName, string SchemaName, string TableName)>
        {
            ("PostgreSql", "pg_ds", "public", "valid_tbl"),
            ("kostentransparenz_dbx", "kostentransparenz_dbx", "controlling", "dim_date"),
            ("legacy_raw", "bad.domain", "public", "tbl"),
            ("accon_oracle", "accon_oracle", "fin", "invoices")
        };

        string? executedSql = null;
        var dialect = new PostgreSqlGovernanceDialect(() => new LightweightRowConnection(rows, sql => executedSql = sql));
        var repo = new GovernanceRepository(dialect, options: Options.Create(new GatewayOptions()));

        var candidates = await repo.GetUnparseableSqlSourceTypesAsync(CancellationToken.None);

        executedSql.ShouldBe("SELECT source_type, source_name, schema_name, table_name FROM TABLES WHERE is_active = 1 AND data_source_type = 0");
        candidates.Count.ShouldBe(2);
        candidates[0].ShouldBe((new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date"), "kostentransparenz_dbx", "kostentransparenz_dbx"));
        candidates[1].ShouldBe((new TableIdentifier("accon_oracle", "fin", "invoices"), "accon_oracle", "accon_oracle"));
    }

    private static TableMetadata BuildSqlTable(TableIdentifier id, string sourceType, bool isActive = true) => new()
    {
        Identifier = id,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            SourceType = sourceType,
            IsActive = isActive,
            DataSourceType = DataSourceType.Sql
        },
        Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }]
    };

    private sealed class FakeCatalogMetadataRepository : ITableMetadataRepository
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _store = new();
        public int GetMetadataCalls { get; private set; }
        public int BatchUpsertCalls { get; private set; }
        public int LastBatchCount { get; private set; }

        public void Seed(TableMetadata metadata) => _store[metadata.Identifier] = metadata;
        public TableMetadata GetStored(TableIdentifier id) => _store[id];

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetMetadataCalls++;
            return Task.FromResult(_store.TryGetValue(table, out var meta) ? meta : null);
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TableMetadata>>(_store.Values.ToList());
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
            IReadOnlyList<TableMetadata> batch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BatchUpsertCalls++;
            LastBatchCount = batch.Count;
            var results = new List<CatalogTableUpsertResult>(batch.Count);
            foreach (var item in batch)
            {
                _store[item.Identifier] = item;
                results.Add(new CatalogTableUpsertResult(item, CatalogUpsertKind.Updated, CatalogWriteClassification.Restrictive));
            }

            return Task.FromResult<IReadOnlyList<CatalogTableUpsertResult>>(results);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class RecordingWriteScopeFactory : ICatalogWriteScopeFactory
    {
        public List<string> BegunWriters { get; } = [];
        public int DisposedCount { get; private set; }

        public ICatalogWriteScope Begin(string writer)
        {
            BegunWriters.Add(writer);
            return new Scope(this, writer);
        }

        private sealed class Scope(RecordingWriteScopeFactory owner, string writer) : ICatalogWriteScope
        {
            public Guid ScopeId { get; } = Guid.NewGuid();
            public string Writer => writer;
            public ValueTask DisposeAsync() { owner.DisposedCount++; return ValueTask.CompletedTask; }
        }
    }

    private sealed class RecordingSnapshotProvider : ICatalogSnapshotProvider
    {
        public int RefreshCalls { get; private set; }
        public CatalogSnapshot CurrentSnapshot { get; } = CatalogSnapshot.Create(new CatalogVersion(1, 1), Array.Empty<TableMetadata>(), Array.Empty<TableRelation>());
        public bool IsInitialized => true;
        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default) => ValueTask.FromResult(CurrentSnapshot);
        public Task RefreshAsync(CancellationToken ct = default) { RefreshCalls++; return Task.CompletedTask; }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class LightweightRowConnection(
        IReadOnlyList<(string SourceType, string SourceName, string SchemaName, string TableName)> rows,
        Action<string> onSql) : DbConnection
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
        protected override DbCommand CreateDbCommand() => new LightweightCommand(rows, onSql, this);
    }

    private sealed class LightweightCommand(
        IReadOnlyList<(string SourceType, string SourceName, string SchemaName, string TableName)> rows,
        Action<string> onSql,
        DbConnection conn) : DbCommand
    {
        private readonly LightweightParameters _parameters = new();
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
        protected override DbParameter CreateDbParameter() => new LightweightParam();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { onSql(CommandText); return new LightweightReader(rows); }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) { onSql(CommandText); return Task.FromResult<DbDataReader>(new LightweightReader(rows)); }
    }

    private sealed class LightweightReader(IReadOnlyList<(string SourceType, string SourceName, string SchemaName, string TableName)> rows) : DbDataReader
    {
        private int _index = -1;
        public override int FieldCount => 4;
        public override int RecordsAffected => rows.Count;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(++_index < rows.Count);
        public override bool NextResult() => false;
        public override int GetOrdinal(string name) => -1;
        public override string GetName(int ordinal) => string.Empty;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override object GetValue(int ordinal) => ordinal switch
        {
            0 => rows[_index].SourceType,
            1 => rows[_index].SourceName,
            2 => rows[_index].SchemaName,
            3 => rows[_index].TableName,
            _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
        };
        public override int GetValues(object[] values) { for (var i = 0; i < 4; i++) values[i] = GetValue(i); return 4; }
        public override bool IsDBNull(int ordinal) => false;
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
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    private sealed class LightweightParam : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
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

    private sealed class LightweightParameters : DbParameterCollection
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
