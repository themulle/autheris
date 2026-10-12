using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) for <see cref="CheckedDmlExecutor"/> and the Databricks executor over a provider fake whose every async
/// call really completes asynchronously on the thread pool, so a changed <c>ConfigureAwait</c> becomes observable through a
/// counting <see cref="SynchronizationContext"/>.
/// </summary>
[Trait("Category", "SqlGate")]
public class CheckedDmlExecutorMutationTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    private static CompiledSql Dml(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer, int? expected = null) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert, ImmutableArray<SecurityPredicateId>.Empty, "test")
        {
            ExpectedAffectedRows = expected
        };

    private static ICheckedDmlExecutor Executor(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => CompiledSqlBinders.CheckedExecutorFor(dialect);

    private sealed class YConn : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;
        public int Affected { get; set; } = 1;
        public int OpenCalls { get; private set; }
        public IsolationLevel? BeganWith { get; private set; }
        public int CommandsCreated { get; private set; }
        public YCmd? LastCommand { get; private set; }
        public YTx? LastTx { get; private set; }
        public CountingContext? Ctx { get; set; }

        public YConn(ConnectionState state = ConnectionState.Open) => _state = state;

        public YTx NewTx() => LastTx = new YTx(this);

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await Hop(Ctx).ConfigureAwait(false);
            OpenCalls++;
            _state = ConnectionState.Open;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) { BeganWith = isolationLevel; return NewTx(); }

        protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            await Hop(Ctx).ConfigureAwait(false);
            BeganWith = isolationLevel;
            return NewTx();
        }

        protected override DbCommand CreateDbCommand()
        {
            CommandsCreated++;
            return LastCommand = new YCmd(this);
        }
    }

    private sealed class YTx(YConn? connection) : DbTransaction
    {
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }
        public bool ThrowOnRollback { get; set; }
        protected override DbConnection? DbConnection => connection;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public override void Commit() => Commits++;
        public override void Rollback() => Rollbacks++;

        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await Hop(connection?.Ctx).ConfigureAwait(false);
            Commits++;
        }

        public override async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await Hop(connection?.Ctx).ConfigureAwait(false);
            Rollbacks++;
            if (ThrowOnRollback) throw new InvalidOperationException("rollback failed");
        }
    }

    private sealed class YCmd : BinderMutationTests.RecCommand
    {
        private readonly YConn _connection;

        public YCmd(YConn connection) : base(() => new BinderMutationTests.RecParameter())
        {
            _connection = connection;
            CommandTimeout = 30;
        }

        public DbTransaction? Tx => Transaction;

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            await Hop(_connection.Ctx).ConfigureAwait(false);
            return _connection.Affected;
        }
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            RunOnNewThread(this, () => d(state));
        }
    }

    private static void RunOnNewThread(SynchronizationContext? context, Action action) =>
        new Thread(() =>
        {
            if (context is not null) SynchronizationContext.SetSynchronizationContext(context);
            action();
        }) { IsBackground = true }.Start();

    /// <summary>
    /// Completes asynchronously on a thread that has <paramref name="context"/> installed. A <c>ConfigureAwait(false)</c> continuation
    /// then runs inline on that thread without capturing it, while a <c>ConfigureAwait(true)</c> one would capture the context at
    /// every await of the executor (not only the first) and post to it.
    /// </summary>
    private static Task Hop(CountingContext? context)
    {
        var done = new TaskCompletionSource();
        RunOnNewThread(context, () => done.SetResult());
        return done.Task;
    }

    /// <summary>Runs <paramref name="run"/> under a context that counts every continuation posted to it; returns the count.</summary>
    private static int PostsDuring(Func<CountingContext, Task> run)
    {
        var ctx = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            var task = run(ctx);
            try { task.GetAwaiter().GetResult(); } catch (Exception) { /* a failing flow is part of some scenarios */ }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        return ctx.Posts;
    }

    // ---- arguments ----

    [Fact]
    public async Task ExecuteAsync_ChecksItsArguments()
    {
        var conn = new YConn();
        var sql = Dml("UPDATE t SET a = 1");
        Assert.Equal("connection", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor().ExecuteAsync(null!, sql, NoValues))).ParamName);
        Assert.Equal("compiled", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor().ExecuteAsync(conn, null!, NoValues))).ParamName);
        Assert.Equal("clientParameterValues", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor().ExecuteAsync(conn, sql, null!))).ParamName);
        Assert.Equal(0, conn.CommandsCreated);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Databricks)]
    public async Task ExecuteInTransactionAsync_ChecksItsArguments(TargetSqlDialect dialect)
    {
        var conn = new YConn();
        var tx = conn.NewTx();
        var sql = Dml("UPDATE t SET a = 1", dialect);
        var limits = new CheckedDmlLimits(0, 0);
        Assert.Equal("transaction", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor(dialect).ExecuteInTransactionAsync(null!, sql, NoValues, limits))).ParamName);
        Assert.Equal("compiled", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor(dialect).ExecuteInTransactionAsync(tx, null!, NoValues, limits))).ParamName);
        Assert.Equal("clientParameterValues", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor(dialect).ExecuteInTransactionAsync(tx, sql, null!, limits))).ParamName);
        Assert.Equal("limits", (await Assert.ThrowsAsync<ArgumentNullException>(() => Executor(dialect).ExecuteInTransactionAsync(tx, sql, NoValues, null!))).ParamName);
        Assert.Equal(0, conn.CommandsCreated);
        Assert.Equal(0, tx.Rollbacks);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Databricks)]
    public async Task NegativeLimits_AreRejected_ButZeroIsAllowed(TargetSqlDialect dialect)
    {
        var conn = new YConn();
        var sql = Dml("UPDATE t SET a = 1", dialect);
        var timeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Executor(dialect).ExecuteInTransactionAsync(conn.NewTx(), sql, NoValues, new CheckedDmlLimits(-1, 0)));
        Assert.Equal("limits", timeout.ParamName);
        Assert.StartsWith("The command timeout must not be negative.", timeout.Message);
        var rows = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Executor(dialect).ExecuteInTransactionAsync(conn.NewTx(), sql, NoValues, new CheckedDmlLimits(0, -1)));
        Assert.Equal("limits", rows.ParamName);
        Assert.StartsWith("The affected-row limit must not be negative.", rows.Message);
        Assert.Equal(0, conn.CommandsCreated);

        Assert.Equal(1, await Executor(dialect).ExecuteInTransactionAsync(conn.NewTx(), sql, NoValues, new CheckedDmlLimits(0, 0)));
    }

    // ---- dialect ----

    [Fact]
    public async Task ExecuteAsync_StatementOfAnotherDialect_IsRefusedBeforeAnythingRuns()
    {
        var conn = new YConn(ConnectionState.Closed);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Executor().ExecuteAsync(conn, Dml("UPDATE t SET a = 1", TargetSqlDialect.Sqlite), NoValues));
        Assert.Equal("The compiled SQL was generated for another dialect.", ex.Message);
        Assert.Equal(0, conn.OpenCalls);
        Assert.Null(conn.BeganWith);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_StatementOfAnotherDialect_RollsBackWithTheDialectMessage()
    {
        var conn = new YConn();
        var tx = conn.NewTx();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteInTransactionAsync(tx, Dml("UPDATE t SET a = 1", TargetSqlDialect.Sqlite), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("The compiled SQL was generated for another dialect.", ex.Message);
        Assert.Equal(1, tx.Rollbacks);
        Assert.Equal(0, tx.Commits);
        Assert.Equal(0, conn.CommandsCreated);
    }

    [Fact]
    public async Task DatabricksExecutor_StatementOfAnotherDialect_IsRefusedBeforeAnyCommand()
    {
        var conn = new YConn();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(conn.NewTx(), Dml("UPDATE t SET a = 1", TargetSqlDialect.Sqlite), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("The compiled SQL was generated for another dialect.", ex.Message);
        Assert.Equal(0, conn.CommandsCreated);
    }

    // ---- connection overload ----

    [Fact]
    public async Task ExecuteAsync_OpensAClosedConnection_RunsInATransactionOfTheRequestedIsolation_AndCommits()
    {
        var conn = new YConn(ConnectionState.Closed) { Affected = 1 };
        int affected = await Executor().ExecuteAsync(conn, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues, IsolationLevel.Serializable);
        Assert.Equal(1, affected);
        Assert.Equal(1, conn.OpenCalls);
        Assert.Equal(IsolationLevel.Serializable, conn.BeganWith);
        Assert.Equal(1, conn.LastTx!.Commits);
        Assert.Equal(0, conn.LastTx.Rollbacks);
        Assert.Equal("INSERT INTO t (a) SELECT 1", conn.LastCommand!.CommandText);
        Assert.Same(conn.LastTx, conn.LastCommand.Tx);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotReopenAnOpenConnection()
    {
        var conn = new YConn();
        await Executor().ExecuteAsync(conn, Dml("UPDATE t SET a = 1"), NoValues);
        Assert.Equal(0, conn.OpenCalls);
        Assert.Equal(IsolationLevel.ReadCommitted, conn.BeganWith);
        Assert.Equal(30, conn.LastCommand!.CommandTimeout);   // the connection overload never sets a command timeout
    }

    [Fact]
    public async Task ExecuteAsync_RowCountMismatch_RollsBackAndDoesNotCommit()
    {
        var conn = new YConn { Affected = 2 };
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => Executor().ExecuteAsync(conn, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues));
        Assert.Equal(0, conn.LastTx!.Commits);
        Assert.Equal(1, conn.LastTx.Rollbacks);
    }

    // ---- transaction overload ----

    [Fact]
    public async Task ExecuteInTransactionAsync_ATransactionOnAClosedConnection_IsRejectedWithoutCompletingIt()
    {
        var conn = new YConn(ConnectionState.Closed);
        var tx = conn.NewTx();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteInTransactionAsync(tx, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("The transaction is completed or its connection is not open.", ex.Message);
        Assert.Equal(0, tx.Rollbacks);
        Assert.Equal(0, tx.Commits);

        var detached = new YTx(null);
        Assert.Equal("The transaction is completed or its connection is not open.", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteInTransactionAsync(detached, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(0, 0)))).Message);
    }

    [Fact]
    public async Task DatabricksExecutor_ATransactionWithoutAConnection_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(new YTx(null), Dml("UPDATE t SET a = 1", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("The transaction is completed.", ex.Message);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 0, 30)]
    [InlineData(TargetSqlDialect.SqlServer, 7, 7)]
    [InlineData(TargetSqlDialect.Databricks, 0, 30)]
    [InlineData(TargetSqlDialect.Databricks, 7, 7)]
    public async Task CommandTimeout_IsOnlySetWhenPositive(TargetSqlDialect dialect, int timeout, int expected)
    {
        var conn = new YConn();
        await Executor(dialect).ExecuteInTransactionAsync(conn.NewTx(), Dml("UPDATE t SET a = 1", dialect), NoValues, new CheckedDmlLimits(timeout, 0));
        Assert.Equal(expected, conn.LastCommand!.CommandTimeout);
    }

    [Theory]
    [InlineData(0, 5, true)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, false)]
    [InlineData(-1, 5, false)]
    [InlineData(99, 0, true)]
    [InlineData(-1, 0, true)]
    public async Task AffectedRowsLimit_FailsClosedOnAnUnreportedOrExcessiveCount(int affected, long max, bool succeeds)
    {
        var conn = new YConn { Affected = affected };
        var tx = conn.NewTx();
        var run = Executor().ExecuteInTransactionAsync(tx, Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(0, max));
        if (succeeds)
        {
            Assert.Equal(affected, await run);
            Assert.Equal(1, tx.Commits);
            Assert.Equal(0, tx.Rollbacks);
        }
        else
        {
            await Assert.ThrowsAsync<DmlAffectedRowsLimitException>(() => run);
            Assert.Equal(0, tx.Commits);
            Assert.Equal(1, tx.Rollbacks);
        }
    }

    // ---- Databricks ----

    [Fact]
    public async Task DatabricksExecutor_Refusals_CarryTheirOwnDetail()
    {
        var conn = new YConn();
        var limit = await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() =>
            Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(conn.NewTx(), Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(0, 5)));
        Assert.Equal("Not yet supported by the governed SQL compiler: StatementClass (Databricks DML while an affected-row limit is configured).", limit.Message);
        var check = await Assert.ThrowsAsync<SqlCompileNotSupportedException>(() =>
            Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(conn.NewTx(), Dml("INSERT INTO t SELECT 1", TargetSqlDialect.Databricks, expected: 1), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("Not yet supported by the governed SQL compiler: StatementClass (Databricks has no row-count check).", check.Message);
        var unsupported = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Executor(TargetSqlDialect.Databricks).ExecuteAsync(conn, Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues));
        Assert.Equal("Databricks has no transaction; use ExecuteInTransactionAsync.", unsupported.Message);
        Assert.Equal(0, conn.CommandsCreated);
    }

    [Fact]
    public async Task DatabricksExecutor_BindsTheStatementOntoTheCommand_AndReturnsTheCount()
    {
        var conn = new YConn { Affected = 4 };
        var tx = conn.NewTx();
        var compiled = new CompiledSql("DELETE FROM t WHERE a = :p1", ImmutableArray.Create(
                new BoundParameter(":p1", "p1", 0, 9, SqlParameterType.Int32, ParameterOrigin.QueryLiteral)),
            TargetSqlDialect.Databricks, SqlStatementClass.Delete, ImmutableArray<SecurityPredicateId>.Empty, "test");
        Assert.Equal(4, await Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(tx, compiled, NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal("DELETE FROM t WHERE a = :p1", conn.LastCommand!.CommandText);
        Assert.Equal(CommandType.Text, conn.LastCommand.CommandType);
        Assert.Single(conn.LastCommand.Parameters);
        Assert.Equal(9, conn.LastCommand.P(0).Value);
        Assert.Same(tx, conn.LastCommand.Tx);
    }

    // ---- ConfigureAwait(false) on every await of the executors ----

    [Fact]
    public void NoAwaitOfTheExecutors_ResumesOnTheCallersContext()
    {
        int posts = PostsDuring(async ctx =>
        {
            var conn = new YConn(ConnectionState.Closed) { Affected = 1, Ctx = ctx };
            await Executor().ExecuteAsync(conn, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues).ConfigureAwait(false);
        });
        Assert.Equal(0, posts);

        posts = PostsDuring(ctx =>
        {
            var conn = new YConn { Affected = 1, Ctx = ctx };
            return Executor().ExecuteInTransactionAsync(conn.NewTx(), Dml("UPDATE t SET a = 1"), NoValues, new CheckedDmlLimits(5, 5));
        });
        Assert.Equal(0, posts);

        // failure path: the rollback is awaited as well
        posts = PostsDuring(ctx =>
        {
            var conn = new YConn { Affected = 2, Ctx = ctx };
            var tx = conn.NewTx();
            tx.ThrowOnRollback = true;
            return Executor().ExecuteInTransactionAsync(tx, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues, new CheckedDmlLimits(0, 0));
        });
        Assert.Equal(0, posts);

        posts = PostsDuring(ctx =>
        {
            var conn = new YConn { Affected = 1, Ctx = ctx };
            return Executor(TargetSqlDialect.Databricks).ExecuteInTransactionAsync(conn.NewTx(), Dml("DELETE FROM t", TargetSqlDialect.Databricks), NoValues, new CheckedDmlLimits(0, 0));
        });
        Assert.Equal(0, posts);
    }

#if DEBUG
    [Fact]
    public async Task CommitBeforeEnforceSeam_CommitsFirst_AndReturnsWhenTheCheckIsAlsoOff()
    {
        using var seam = CompilerTestSeams.Use(new CompilerTestSeams
        {
            DisabledControls = new HashSet<SecurityControl> { SecurityControl.CheckedDmlCommitBeforeEnforce, SecurityControl.CheckedDmlRowCount }
        });
        var conn = new YConn { Affected = 5 };
        var tx = conn.NewTx();
        Assert.Equal(5, await Executor().ExecuteInTransactionAsync(tx, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues, new CheckedDmlLimits(0, 1)));
        Assert.Equal(1, tx.Commits);   // exactly one commit: the early return skips the limit check and the regular commit
        Assert.Equal(0, tx.Rollbacks);
    }

    [Fact]
    public async Task CommitBeforeEnforceSeam_WithTheCheckOn_CommitsAndThenThrows()
    {
        using var seam = CompilerTestSeams.Use(new CompilerTestSeams
        {
            DisabledControls = new HashSet<SecurityControl> { SecurityControl.CheckedDmlCommitBeforeEnforce }
        });
        var conn = new YConn { Affected = 5 };
        var tx = conn.NewTx();
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
            Executor().ExecuteInTransactionAsync(tx, Dml("INSERT INTO t (a) SELECT 1", expected: 1), NoValues, new CheckedDmlLimits(0, 0)));
        Assert.Equal(1, tx.Commits);
    }
#endif
}
