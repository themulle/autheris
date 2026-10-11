using System.Collections;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0k (G-3, SEC-X1-06, SEC-X1-07): the checked executor runs inside the caller's session transaction and is the single owner of
/// its completion (commit on success, rollback on any failure, including timeout and cancellation).
/// </summary>
public class CheckedDmlExecutorTransactionTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    private static CompiledSql Dml(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer, int? expected = null) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert,
            ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test") { ExpectedAffectedRows = expected };

    private static ICheckedDmlExecutor Executor(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => CompiledSqlBinders.CheckedExecutorFor(dialect);

    private static async Task<SqliteConnection> OpenTableAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (a INTEGER)";
        await create.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<long> CountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- real provider (SQLite in-memory): the caller's transaction is used, never a nested one ----

    [Fact]
    public async Task ExecuteInTransaction_UsesCallerTransaction_NoNestedBegin()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();   // Microsoft.Data.Sqlite refuses a second BeginTransaction
        int affected = await Executor().ExecuteInTransactionAsync(transaction, Dml("INSERT INTO t (a) SELECT 1"), NoValues, new CheckedDmlLimits(30, 0));
        Assert.Equal(1, affected);
        Assert.Equal(1, await CountAsync(connection));   // committed by the executor
    }

    [Fact]
    public async Task ExecuteInTransaction_TransactionCompletedOnce_AfterSuccess()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Executor().ExecuteInTransactionAsync(transaction, Dml("INSERT INTO t (a) SELECT 1"), NoValues, new CheckedDmlLimits(30, 0));
        // the executor completed it; a second completion by the caller is a defect and must fail loudly
        Assert.Null(transaction.Connection);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => transaction.CommitAsync());
    }

    [Fact]
    public async Task ExecuteInTransaction_CheckOptionViolation_RollsBack()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var compiled = Dml("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", expected: 1);
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            Executor().ExecuteInTransactionAsync(transaction, compiled, NoValues, new CheckedDmlLimits(30, 0)));
        Assert.Null(transaction.Connection);
        Assert.Equal(0, await CountAsync(connection));
    }

    [Fact]
    public async Task ExecuteInTransaction_MaxAffectedRowsExceeded_RollsBack()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<DmlAffectedRowsLimitException>(() => Executor().ExecuteInTransactionAsync(
            transaction, Dml("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2"), NoValues, new CheckedDmlLimits(30, 1)));
        Assert.Equal("DML_AFFECTED_ROWS_LIMIT", ex.Code);
        Assert.Equal(TargetSqlDialect.SqlServer, ex.Dialect);
        Assert.DoesNotContain("2", ex.Message);   // fixed message, no counts
        Assert.Null(ex.InnerException);
        Assert.Equal(0, await CountAsync(connection));
    }

    [Fact]
    public async Task ExecuteInTransaction_AffectedRowsAtTheLimit_Commits()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int affected = await Executor().ExecuteInTransactionAsync(
            transaction, Dml("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2"), NoValues, new CheckedDmlLimits(30, 2));
        Assert.Equal(2, affected);
        Assert.Equal(2, await CountAsync(connection));
    }

    [Fact]
    public async Task ExecuteInTransaction_ForeignTransaction_Rejected()
    {
        await using var connection = await OpenTableAsync();
        var transaction = await connection.BeginTransactionAsync();
        await transaction.CommitAsync();   // completed: it belongs to no usable connection any more
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteInTransactionAsync(transaction, Dml("INSERT INTO t (a) SELECT 1"), NoValues, new CheckedDmlLimits(30, 0)));
        Assert.Equal(0, await CountAsync(connection));
    }

    [Fact]
    public async Task ExecuteInTransaction_OtherDialectThanTheExecutor_Rejected_AndRolledBack()
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(TargetSqlDialect.SqlServer).ExecuteInTransactionAsync(
            transaction, Dml("INSERT INTO t (a) SELECT 1", TargetSqlDialect.PostgreSql), NoValues, new CheckedDmlLimits(30, 0)));
        Assert.Null(transaction.Connection);   // still completed by the executor (single owner), nothing written
        Assert.Equal(0, await CountAsync(connection));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(30, -1)]
    public async Task ExecuteInTransaction_NegativeLimits_AreRejected(int timeout, long max)
    {
        await using var connection = await OpenTableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Executor().ExecuteInTransactionAsync(
            transaction, Dml("INSERT INTO t (a) SELECT 1"), NoValues, new CheckedDmlLimits(timeout, max)));
        Assert.Equal(0, await CountAsync(connection));
    }

    // ---- fake provider: counts, timeout, cancellation, unreported counts ----

    [Fact]
    public async Task ExecuteInTransaction_UnreportedCountWithLimit_RollsBack()
    {
        var fake = new FakeConnection { Affected = -1 };
        var transaction = fake.Begin();
        await Assert.ThrowsAsync<DmlAffectedRowsLimitException>(() => Executor().ExecuteInTransactionAsync(
            transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(30, 10)));
        Assert.Equal(0, transaction.Commits);
        Assert.Equal(1, transaction.Rollbacks);
    }

    [Fact]
    public async Task ExecuteInTransaction_UnreportedCountWithoutLimit_Commits()
    {
        var fake = new FakeConnection { Affected = -1 };
        var transaction = fake.Begin();
        int affected = await Executor().ExecuteInTransactionAsync(transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(30, 0));
        Assert.Equal(-1, affected);
        Assert.Equal(1, transaction.Commits);
        Assert.Equal(0, transaction.Rollbacks);
    }

    [Fact]
    public async Task ExecuteInTransaction_CompletesTheTransactionExactlyOnce_AndNeverBeginsOne()
    {
        var fake = new FakeConnection { Affected = 1 };
        var transaction = fake.Begin();
        fake.BeginCalls = 0;
        await Executor().ExecuteInTransactionAsync(transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(30, 5));
        Assert.Equal(0, fake.BeginCalls);
        Assert.Equal(1, transaction.Commits);
        Assert.Equal(0, transaction.Rollbacks);
        Assert.Same(transaction, fake.LastCommand!.Transaction);
    }

    [Fact]
    public async Task ExecuteInTransaction_AppliesTheCommandTimeout()
    {
        var fake = new FakeConnection { Affected = 1 };
        await Executor().ExecuteInTransactionAsync(fake.Begin(), Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(17, 0));
        Assert.Equal(17, fake.LastCommand!.CommandTimeout);
    }

    [Fact]
    public async Task ExecuteInTransaction_Timeout_RollsBack_AndKeepsTheOriginalException()
    {
        var fake = new FakeConnection { OnExecute = _ => throw new TimeoutException("simulated command timeout") };
        var transaction = fake.Begin();
        await Assert.ThrowsAsync<TimeoutException>(() => Executor().ExecuteInTransactionAsync(
            transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(1, 0)));
        Assert.Equal(0, transaction.Commits);
        Assert.Equal(1, transaction.Rollbacks);
    }

    [Fact]
    public async Task ExecuteInTransaction_Cancellation_RollsBack_WithoutTheCancelledToken()
    {
        var fake = new FakeConnection { OnExecute = ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 0, ct) };
        var transaction = fake.Begin();
        using var cts = new CancellationTokenSource();
        var run = Executor().ExecuteInTransactionAsync(transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(30, 0), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, transaction.Commits);
        Assert.Equal(1, transaction.Rollbacks);
        Assert.False(transaction.LastRollbackToken.CanBeCanceled);   // the rollback must not be cancelled by the request token
    }

    [Fact]
    public async Task ExecuteInTransaction_FailingRollback_DoesNotMaskTheOriginalError()
    {
        var fake = new FakeConnection { OnExecute = _ => throw new TimeoutException("original") };
        var transaction = fake.Begin();
        transaction.ThrowOnRollback = true;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Executor().ExecuteInTransactionAsync(
            transaction, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(30, 0)));
        Assert.Equal("original", ex.Message);
    }

    // ---- Databricks: no transaction, S-2 ----

    [Fact]
    public async Task DatabricksExecutor_RefusesDmlWhileLimitConfigured()
    {
        var fake = new FakeConnection { Affected = 1 };
        var ex = await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() => Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(
            fake.Begin(), Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(30, 100)));
        Assert.Equal(SqlCompileNotSupportedReason.StatementClass, ex.Reason);
    }

    [Fact]
    public async Task DatabricksExecutor_RejectsBeforeAnyHttpRequest()
    {
        // the facade connection is the only thing that sends a request: no command may even be created
        var fake = new FakeConnection { Affected = 1 };
        var transaction = fake.Begin();
        fake.BeginCalls = 0;
        await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() => Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(
            transaction, Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(30, 100)));
        Assert.Equal(0, fake.CommandsCreated);
        Assert.Equal(0, fake.BeginCalls);
        Assert.Equal(0, transaction.Commits);
    }

    [Fact]
    public async Task DatabricksExecutor_WithoutALimit_RunsNonTransactionally()
    {
        var fake = new FakeConnection { Affected = 3 };
        var transaction = fake.Begin();
        int affected = await Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(
            transaction, Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(30, 0));
        Assert.Equal(3, affected);
        Assert.Equal(1, fake.CommandsCreated);
        Assert.Equal(0, transaction.Commits);     // Delta has no transaction to complete
        Assert.Equal(0, transaction.Rollbacks);
    }

    [Fact]
    public async Task DatabricksExecutor_RefusesARowCountCheck()
    {
        var fake = new FakeConnection { Affected = 1 };
        await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() => Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(
            fake.Begin(), Dml("INSERT INTO t SELECT 1", TargetSqlDialect.Databricks, expected: 1), NoValues, new CheckedDmlLimits(30, 0)));
        Assert.Equal(0, fake.CommandsCreated);
    }

    [Fact]
    public async Task DatabricksExecutor_ConnectionOverload_IsNotSupported()
    {
        var fake = new FakeConnection();
        await Assert.ThrowsAsync<NotSupportedException>(() => Executor(TargetSqlDialect.Databricks).ExecuteAsync(
            fake, Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues));
        Assert.Equal(0, fake.CommandsCreated);
    }

    // ---- binder lookup ----

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void CompiledSqlBinders_EveryProductionDialect_ReturnsRefusingBinder(TargetSqlDialect dialect)
    {
        var binder = CompiledSqlBinders.For(dialect);
        Assert.True(binder.CanBind(dialect));
        Assert.Same(binder, CompiledSqlBinders.For(dialect));   // stateless singleton
        using var cmd = new SqliteCommand();
        var checkedStatement = Dml("INSERT INTO t SELECT 1", dialect, expected: 1);
        Assert.Throws<CheckedExecutionRequiredException>(() => binder.Bind(cmd, checkedStatement, NoValues));
        Assert.NotNull(CompiledSqlBinders.CheckedExecutorFor(dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.Snowflake)]
    [InlineData(TargetSqlDialect.Ansi)]
    public void CompiledSqlBinders_Snowflake_Throws(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.For(dialect));
        Assert.Equal(SqlCompileNotSupportedReason.Dialect, ex.Reason);
        Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.CheckedExecutorFor(dialect));
    }

    // ---- minimal ADO.NET fake ----

    private sealed class FakeConnection : DbConnection
    {
        public int Affected { get; set; }
        public Func<CancellationToken, Task<int>>? OnExecute { get; set; }
        public int BeginCalls { get; set; }
        public int CommandsCreated { get; private set; }
        public FakeCommand? LastCommand { get; private set; }

        public FakeTransaction Begin() { BeginCalls++; return new FakeTransaction(this); }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Begin();
        protected override DbCommand CreateDbCommand()
        {
            CommandsCreated++;
            return LastCommand = new FakeCommand(this);
        }
    }

    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }
        public bool ThrowOnRollback { get; set; }
        public CancellationToken LastRollbackToken { get; private set; } = new CancellationToken(true);
        protected override DbConnection DbConnection => connection;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public override void Commit() => Commits++;
        public override void Rollback() { Rollbacks++; if (ThrowOnRollback) throw new InvalidOperationException("rollback failed"); }
        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            LastRollbackToken = cancellationToken;
            Rollback();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        private readonly FakeParameters _parameters = new();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => connection.Affected;
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            connection.OnExecute is { } run ? run(cancellationToken) : Task.FromResult(connection.Affected);
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
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
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }
}
