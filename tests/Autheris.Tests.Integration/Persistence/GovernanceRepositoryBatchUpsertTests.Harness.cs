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
using Npgsql;

public sealed partial class GovernanceRepositoryBatchUpsertTests
{
    internal sealed record RecordedCommand(string Sql, IReadOnlyDictionary<string, object?> Params);

    private sealed class RecordingBatchHarness(string connectionString)
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
            return new RecordingNpgsqlConnection(this, new NpgsqlConnection(connectionString));
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
        public int CalculateEffectiveChunkSize(int defaultChunkSize, int keyColumnCount, int contextParameterCount = 0, DatabaseDialect dialect = DatabaseDialect.SqlServer, int safetyBuffer = 50)
        {
            Calls++;
            return Math.Min(defaultChunkSize, chunkSize);
        }
    }

    private sealed class CustomBudgetDialect(IParameterBudgetProvider budget, Func<DbConnection> connFactory) : IGovernanceSqlDialect
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
        public bool IsTransient(DbException ex) => ex is FakeTransientDeadlockException;
        public IParameterBudgetProvider ParameterBudget => budget;
        public DbConnection CreateConnection() => connFactory();
    }

    private sealed class RecordingNpgsqlConnection(RecordingBatchHarness harness, NpgsqlConnection inner) : DbConnection
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
            new RecordingNpgsqlTransaction(harness, inner.BeginTransaction(isolationLevel));
        protected override DbCommand CreateDbCommand() =>
            new RecordingNpgsqlCommand(harness, inner.CreateCommand());

        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class RecordingNpgsqlTransaction(RecordingBatchHarness harness, NpgsqlTransaction inner) : DbTransaction
    {
        public NpgsqlTransaction Inner => inner;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection? DbConnection => inner.Connection;
        public override void Commit() { harness.OnCommit(); inner.Commit(); }
        public override Task CommitAsync(CancellationToken cancellationToken = default) { harness.OnCommit(); return inner.CommitAsync(cancellationToken); }
        public override void Rollback() { harness.OnRollback(); inner.Rollback(); }
        public override Task RollbackAsync(CancellationToken cancellationToken = default) { harness.OnRollback(); return inner.RollbackAsync(cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class RecordingNpgsqlCommand(RecordingBatchHarness harness, NpgsqlCommand inner) : DbCommand
    {
        private string _rawSql = string.Empty;
        private RecordingNpgsqlTransaction? _tx;

        [AllowNull]
        public override string CommandText { get => _rawSql; set { _rawSql = value ?? string.Empty; inner.CommandText = _rawSql; } }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = (NpgsqlConnection?)value; }
        protected override DbTransaction? DbTransaction
        {
            get => _tx;
            set { _tx = value as RecordingNpgsqlTransaction; if (_tx is not null) inner.Transaction = _tx.Inner; }
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
            foreach (NpgsqlParameter p in inner.Parameters)
            {
                var name = p.ParameterName.StartsWith('@') ? p.ParameterName : "@" + p.ParameterName;
                dict[name] = p.Value == DBNull.Value ? null : p.Value;
            }
            harness.Commands.Add(new RecordedCommand(_rawSql, dict));
        }

        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
    }
}
