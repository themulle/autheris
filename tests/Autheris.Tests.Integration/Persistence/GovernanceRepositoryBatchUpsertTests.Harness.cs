namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Data.Sqlite;

public sealed partial class GovernanceRepositoryBatchUpsertTests
{
    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE TABLES (
                id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                source_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                sensitivity TEXT NOT NULL,
                requires_four_eyes INTEGER NOT NULL,
                is_active INTEGER NOT NULL,
                data_source_type INTEGER NOT NULL,
                http_endpoint_json TEXT NULL,
                plugin_name TEXT NULL,
                description TEXT NULL,
                long_description TEXT NULL,
                doc_source TEXT NULL,
                required_filter_columns_json TEXT NULL,
                texts_json TEXT NULL,
                min_group_size INTEGER NULL,
                non_personal INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX idx_tables_natural ON TABLES (source_name, schema_name, table_name);
            CREATE TABLE TABLE_COLUMNS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                data_type TEXT NOT NULL,
                is_sensitive INTEGER NOT NULL,
                description TEXT NULL,
                long_description TEXT NULL,
                doc_source TEXT NULL,
                meta_json TEXT NULL,
                display_name TEXT NULL,
                texts_json TEXT NULL,
                "collation" TEXT NULL,
                length_semantics TEXT NULL,
                ordinal INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE COLUMN_MASKING_RULES (
                id TEXT PRIMARY KEY,
                table_column_id TEXT NOT NULL,
                rule_type TEXT NOT NULL,
                pattern_or_format TEXT NULL,
                replacement TEXT NULL,
                hmac_key_id TEXT NULL
            );
            CREATE TABLE POLICY_EPOCHS (
                table_id TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                change_version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_policy_epochs_change_version ON POLICY_EPOCHS (change_version);
            CREATE TABLE catalog_state (
                id INTEGER PRIMARY KEY,
                structure_version INTEGER NOT NULL,
                content_version INTEGER NOT NULL,
                updated_at TEXT NULL
            );
            INSERT INTO catalog_state (id, structure_version, content_version) VALUES (1, 1, 1);
            CREATE TABLE TABLE_CATALOG_SETTINGS (
                table_id TEXT PRIMARY KEY,
                visibility INTEGER NOT NULL,
                min_group_size INTEGER NULL,
                non_personal INTEGER NULL,
                discovery_hidden INTEGER NOT NULL DEFAULT 0,
                samples_disabled INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NULL,
                updated_by TEXT NULL
            );
            CREATE TABLE COLUMN_CATALOG_SETTINGS (
                table_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                discovery_hidden INTEGER NOT NULL DEFAULT 0,
                updated_by TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (table_id, column_name)
            );
            CREATE TABLE TABLE_TAGS (
                table_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                source TEXT NOT NULL
            );
            CREATE TABLE TABLE_ROW_FILTER_STRATEGIES (
                table_id TEXT PRIMARY KEY,
                strategy TEXT NULL,
                duration_seconds INTEGER NULL,
                max_keys INTEGER NULL
            );
            CREATE TABLE TABLE_PRIMARY_KEY_COLUMNS (
                table_id TEXT NOT NULL,
                key_ordinal INTEGER NOT NULL,
                table_column_id TEXT NOT NULL,
                is_descending INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (table_id, key_ordinal)
            );
            CREATE TABLE TABLE_INDEXES (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                index_name TEXT NOT NULL,
                is_unique INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE TABLE_INDEX_COLUMNS (
                index_id TEXT NOT NULL,
                column_ordinal INTEGER NOT NULL,
                table_column_id TEXT NOT NULL,
                is_descending INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (index_id, column_ordinal)
            );
            CREATE TABLE TABLE_PHYSICAL_INDEXES (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                index_ordinal INTEGER NOT NULL
            );
            CREATE TABLE TABLE_PHYSICAL_INDEX_COLUMNS (
                index_id TEXT NOT NULL,
                column_ordinal INTEGER NOT NULL,
                table_column_id TEXT NOT NULL,
                is_descending INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (index_id, column_ordinal)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    internal sealed record RecordedCommand(string Sql, IReadOnlyDictionary<string, object?> Params);

    private sealed class SqliteBatchHarness(string dbName)
    {
        public int ConnectionOpenCount { get; private set; }
        public int CommittedTransactions { get; private set; }
        public int RolledBackTransactions { get; private set; }
        public int TransientFailuresRemaining { get; set; }
        public List<RecordedCommand> Commands { get; } = new();

        public void ResetCounters()
        {
            CommittedTransactions = 0;
            RolledBackTransactions = 0;
            Commands.Clear();
        }

        public DbConnection CreateConnection()
        {
            ConnectionOpenCount++;
            return new TranslatingSqliteConnection(this, new SqliteConnection($"Data Source={dbName}"));
        }

        public void OnCommit()
        {
            if (TransientFailuresRemaining > 0)
            {
                TransientFailuresRemaining--;
                throw new FakeTransientDeadlockException("Simulated serialization/deadlock failure");
            }

            CommittedTransactions++;
        }

        public void OnRollback() => RolledBackTransactions++;
    }

    private sealed class FakeTransientDeadlockException(string message) : DbException(message)
    {
        public override bool IsTransient => true;
        public override string? SqlState => "40P01";
    }

    private sealed class RecordingEpochValidationService : IEpochValidationService
    {
        public List<TableIdentifier> Invalidations { get; } = new();
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) { Invalidations.Add(table); return Task.CompletedTask; }
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public int CatalogChangedCount { get; set; }
        public event Action? ConnectionRestored { add { } remove { } }
        public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
        {
            if (channel == ICatalogVersionProvider.CatalogChangedEventChannel)
            {
                CatalogChangedCount++;
            }

            return Task.CompletedTask;
        }
        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) => SubstituteDispose.Instance;
        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        private sealed class SubstituteDispose : IDisposable { public static readonly SubstituteDispose Instance = new(); public void Dispose() { } }
    }

    private sealed class FixedChunkBudgetProvider(int chunkSize) : IParameterBudgetProvider
    {
        public int Calls { get; private set; }
        public int GetMaxParameters(DatabaseDialect dialect) => chunkSize * 3 + 50;
        public int CalculateEffectiveChunkSize(int defaultChunkSize, int keyColumnCount, int contextParameterCount = 0, DatabaseDialect dialect = DatabaseDialect.Sqlite, int safetyBuffer = 50)
        {
            Calls++;
            return Math.Min(defaultChunkSize, chunkSize);
        }
    }

    private sealed class CustomBudgetDialect(IParameterBudgetProvider budget, Func<DbConnection> connFactory) : IGovernanceSqlDialect
    {
        public string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns) => string.Empty;
        public string InsertReturning(string column) => string.Empty;
        public string LockRowsForUpdate => string.Empty;
        public string UtcNow => "CURRENT_TIMESTAMP";
        public string JsonValue(string column, string path) => column;
        public string StringAggregate(string expr, string separator) => expr;
        public string LimitWithoutOrder(int n) => string.Empty;
        public bool IsUniqueViolation(DbException ex) => false;
        public bool IsTransient(DbException ex) => ex is FakeTransientDeadlockException;
        public IParameterBudgetProvider ParameterBudget => budget;
        public DbConnection CreateConnection() => connFactory();
    }

    private sealed class TranslatingSqliteConnection(SqliteBatchHarness harness, SqliteConnection inner) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value ?? string.Empty; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void Open() => inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
        public override void Close() => inner.Close();
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new TranslatingSqliteTransaction(harness, inner.BeginTransaction(isolationLevel));
        protected override DbCommand CreateDbCommand() =>
            new TranslatingSqliteCommand(harness, inner.CreateCommand());

        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class TranslatingSqliteTransaction(SqliteBatchHarness harness, SqliteTransaction inner) : DbTransaction
    {
        public SqliteTransaction Inner => inner;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection? DbConnection => inner.Connection;
        public override void Commit() { harness.OnCommit(); inner.Commit(); }
        public override Task CommitAsync(CancellationToken cancellationToken = default) { harness.OnCommit(); return inner.CommitAsync(cancellationToken); }
        public override void Rollback() { harness.OnRollback(); inner.Rollback(); }
        public override Task RollbackAsync(CancellationToken cancellationToken = default) { harness.OnRollback(); return inner.RollbackAsync(cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class TranslatingSqliteCommand(SqliteBatchHarness harness, SqliteCommand inner) : DbCommand
    {
        private string _rawSql = string.Empty;
        private TranslatingSqliteTransaction? _tx;

        [AllowNull]
        public override string CommandText { get => _rawSql; set { _rawSql = value ?? string.Empty; inner.CommandText = TranslatePgSqlToSqlite(_rawSql); } }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = (SqliteConnection?)value; }
        protected override DbTransaction? DbTransaction
        {
            get => _tx;
            set { _tx = value as TranslatingSqliteTransaction; if (_tx is not null) inner.Transaction = _tx.Inner; }
        }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        public override int ExecuteNonQuery() { Record(); return inner.ExecuteNonQuery(); }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) { Record(); return inner.ExecuteNonQueryAsync(cancellationToken); }
        public override object? ExecuteScalar() { Record(); return inner.ExecuteScalar(); }
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) { Record(); return inner.ExecuteScalarAsync(cancellationToken); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { Record(); return inner.ExecuteReader(behavior); }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) { Record(); return await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false); }

        private void Record()
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (SqliteParameter p in inner.Parameters)
                dict[p.ParameterName] = p.Value == DBNull.Value ? null : p.Value;
            harness.Commands.Add(new RecordedCommand(_rawSql, dict));
        }

        private static string TranslatePgSqlToSqlite(string sql)
        {
            var s = sql.Replace("::timestamptz", string.Empty, StringComparison.Ordinal);
            if (s.Contains("to_regclass(@name) IS NOT NULL", StringComparison.Ordinal))
                return "SELECT CASE WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND LOWER(name)=LOWER(@name)) THEN 1 ELSE 0 END";
            return s.EndsWith(" FOR UPDATE", StringComparison.Ordinal) ? s[..^" FOR UPDATE".Length] : s;
        }

        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }
}
