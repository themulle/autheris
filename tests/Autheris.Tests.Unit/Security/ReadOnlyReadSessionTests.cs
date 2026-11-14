namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Sql;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>
/// WP-OI-SEC-DP-14 (#505, SEC-OI-505-5, SEC-OI-505-6): User reads run in explicit database read-only transactions on
/// PostgreSQL and Oracle (<c>SET TRANSACTION READ ONLY</c>), rolled back cleanly before connections return to the pool,
/// without leaking onto pooled connections or write paths.
/// </summary>
[Trait("Category", "Security")]
public sealed class ReadOnlyReadSessionTests
{
    private const string Tenant = "tenant_a";
    private const string DataSource = "default";

    private static ClaimsPrincipal Caller() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-READ"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, "Writer"),
                new Claim("scp", "Sql.Write")
            ],
            "Test"));

    private static GatewayOptions Gateway(string provider) => new()
    {
        WebSql = new WebSqlOptions
        {
            Enabled = true,
            AllowDml = true,
            DmlWriterRoles = ["Writer"],
            DefaultDataSourceName = DataSource,
            MaxAffectedRows = 1000,
            ExecutionTimeoutSeconds = 30
        },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [DataSource] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "Host=unused" }
            }
        }
    };

    private static CompiledSql CompiledSelect(TargetSqlDialect dialect, string sql = "SELECT \"id\" FROM \"orders\"") =>
        new(
            sql,
            ImmutableArray<BoundParameter>.Empty,
            dialect,
            SqlStatementClass.Select,
            ImmutableArray<SecurityPredicateId>.Empty,
            "1.0");

    private static CompiledSql CompiledUpdate(TargetSqlDialect dialect, string sql = "UPDATE \"orders\" SET \"amount\" = 1 WHERE \"id\" = 1") =>
        new(
            sql,
            ImmutableArray<BoundParameter>.Empty,
            dialect,
            SqlStatementClass.Update,
            ImmutableArray<SecurityPredicateId>.Empty,
            "1.0");

    private static (GovernedSqlExecutor Executor, PooledBackendState Backend) CreateExecutor(string provider, PooledBackendState? backend = null)
    {
        backend ??= new PooledBackendState();
        var options = Options.Create(Gateway(provider));
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(backend.RentConnection()));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var executor = new GovernedSqlExecutor(
            new GovernedSqlRewriter(options, sqlEngine: new FastSqlEngine()),
            options,
            AuditWriterTestServices.Over(auditRepo),
            PassThroughExecutionGuard.Instance,
            factory,
            environment: null,
            logger: NullLogger<GovernedSqlExecutor>.Instance,
            sessionInitializer: new DbSessionContextInitializer());

        return (executor, backend);
    }

    [Fact]
    public async Task Select_runs_in_read_only_transaction_on_postgresql()
    {
        var (executor, backend) = CreateExecutor("PostgreSql");
        var compiled = CompiledSelect(TargetSqlDialect.PostgreSql, "SELECT \"id\" FROM \"orders\"");

        await executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT id FROM orders"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(compiled, [], DataSource),
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        var rental = backend.Rentals.ShouldHaveSingleItem();
        rental.Commands.Count.ShouldBe(3);
        rental.Commands[0].Sql.ShouldBe("SET TRANSACTION READ ONLY");
        rental.Commands[0].HasTransaction.ShouldBeTrue();
        rental.Commands[1].Sql.ShouldContain("set_config('autheris.tenant_id', @tenant, true)");
        rental.Commands[1].HasTransaction.ShouldBeTrue();
        rental.Commands[2].Sql.ShouldBe("SELECT \"id\" FROM \"orders\"");
        rental.Commands[2].HasTransaction.ShouldBeTrue();

        // PostgreSQL: never mutates session-level defaults.
        rental.Commands.ShouldNotContain(c =>
            c.Sql.Contains("SET SESSION CHARACTERISTICS", StringComparison.OrdinalIgnoreCase) ||
            c.Sql.Contains("default_transaction_read_only", StringComparison.OrdinalIgnoreCase));

        rental.RollbackHappenedBeforeConnectionClose.ShouldBeTrue();
        rental.Transactions.ShouldHaveSingleItem().CommitCount.ShouldBe(0);
        rental.Transactions[0].RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task Select_runs_in_read_only_transaction_on_oracle()
    {
        var (executor, backend) = CreateExecutor("Oracle");
        var compiled = CompiledSelect(TargetSqlDialect.Oracle, "SELECT \"ID\" FROM \"ORDERS\"");

        await executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT id FROM orders"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(compiled, [], DataSource),
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        var rental = backend.Rentals.ShouldHaveSingleItem();
        rental.Commands.Count.ShouldBe(3);
        rental.Commands[0].Sql.ShouldBe("SET TRANSACTION READ ONLY");
        rental.Commands[0].HasTransaction.ShouldBeTrue();
        rental.Commands[1].Sql.ShouldContain("DBMS_SESSION.SET_IDENTIFIER");
        rental.Commands[1].HasTransaction.ShouldBeTrue();
        rental.Commands[2].Sql.ShouldBe("SELECT \"ID\" FROM \"ORDERS\"");
        rental.Commands[2].HasTransaction.ShouldBeTrue();

        rental.RollbackHappenedBeforeConnectionClose.ShouldBeTrue();
        var tx = rental.Transactions.ShouldHaveSingleItem();
        tx.CommitCount.ShouldBe(0);
        tx.RollbackCount.ShouldBe(1);
        backend.HasDanglingActiveTransaction.ShouldBeFalse();
    }

    [Theory]
    [InlineData("PostgreSql", TargetSqlDialect.PostgreSql)]
    [InlineData("Oracle", TargetSqlDialect.Oracle)]
    public async Task Volatile_function_write_is_refused_by_database_on_read_path(string provider, TargetSqlDialect dialect)
    {
        var backend = new PooledBackendState();
        var (executor, _) = CreateExecutor(provider, backend);
        var compiled = CompiledSelect(dialect, "SELECT nextval('md.side_effect')");

        var ex = await Should.ThrowAsync<GovernedSqlException>(() => executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT nextval('md.side_effect')"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(compiled, [], DataSource),
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.ProviderError);
        backend.ReadOnlyViolationCount.ShouldBe(1);
        var rental = backend.Rentals.ShouldHaveSingleItem();
        rental.Commands[0].Sql.ShouldBe("SET TRANSACTION READ ONLY");
        rental.RollbackHappenedBeforeConnectionClose.ShouldBeTrue();
        rental.Transactions.ShouldHaveSingleItem().CommitCount.ShouldBe(0);
        rental.Transactions[0].RollbackCount.ShouldBe(1);
        backend.HasDanglingActiveTransaction.ShouldBeFalse();
    }

    [Theory]
    [InlineData("PostgreSql", TargetSqlDialect.PostgreSql)]
    [InlineData("Oracle", TargetSqlDialect.Oracle)]
    public async Task Pooled_connection_after_read_only_read_accepts_dml_on_write_path(string provider, TargetSqlDialect dialect)
    {
        var backend = new PooledBackendState();
        var (executor, _) = CreateExecutor(provider, backend);

        // 1. User read on pooled connection enters SET TRANSACTION READ ONLY and rolls back before pool return.
        await executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT id FROM orders"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(CompiledSelect(dialect), [], DataSource),
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        // 2. Subsequent rental of the exact same pooled connection for a DML write path must not be poisoned read-only.
        var dmlContext = new DmlAuditContext
        {
            StatementType = TrinoSqlEngine.Analysis.SqlStatementType.Update,
            Tables = ["orders"]
        };

        await executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("UPDATE orders SET amount = 1 WHERE id = 1"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(CompiledUpdate(dialect), [], DataSource),
            dmlContext,
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        backend.Rentals.Count.ShouldBe(2);
        backend.SessionLevelReadOnlyPoisoned.ShouldBeFalse();
        backend.HasDanglingActiveTransaction.ShouldBeFalse();

        var writeRental = backend.Rentals[1];
        writeRental.Commands.ShouldNotContain(c => c.Sql.Contains("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase));
        var writeTx = writeRental.Transactions.ShouldHaveSingleItem();
        writeTx.IsReadOnly.ShouldBeFalse();
        writeTx.CommitCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.Oracle)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.Sqlite)]
    [InlineData(DatabaseDialect.Databricks)]
    public async Task Read_only_mode_not_set_without_explicit_request(DatabaseDialect dialect)
    {
        var backend = new PooledBackendState();
        await using var conn = backend.RentConnection();
        var sut = new DbSessionContextInitializer();

        await using var tx = await sut.InitializeSessionAsync(
            conn,
            dialect,
            new TenantId(Tenant),
            userSid: "S-1-5-21-1",
            purpose: null,
            requireTransaction: dialect == DatabaseDialect.PostgreSql);

        var rental = backend.Rentals.ShouldHaveSingleItem();
        rental.Commands.ShouldNotContain(c =>
            c.Sql.Contains("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase) ||
            c.Sql.Contains("PRAGMA query_only", StringComparison.OrdinalIgnoreCase));

        if (tx != null)
        {
            await tx.RollbackAsync();
        }
    }

    [Theory]
    [InlineData("PostgreSql", TargetSqlDialect.PostgreSql)]
    [InlineData("Oracle", TargetSqlDialect.Oracle)]
    public async Task Failure_to_set_mode_fails_the_read(string provider, TargetSqlDialect dialect)
    {
        var backend = new PooledBackendState { FailOnSetTransactionReadOnly = true };
        var (executor, _) = CreateExecutor(provider, backend);

        await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT id FROM orders"),
            Caller(),
            new TenantId(Tenant),
            new GovernedRewrite(CompiledSelect(dialect, "SELECT \"id\" FROM \"orders\""), [], DataSource),
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None));

        var rental = backend.Rentals.ShouldHaveSingleItem();
        rental.Commands.Select(c => c.Sql).ShouldBe(["SET TRANSACTION READ ONLY"]);
        rental.Transactions.ShouldHaveSingleItem().RollbackCount.ShouldBe(1);
        rental.Transactions[0].CommitCount.ShouldBe(0);
        backend.HasDanglingActiveTransaction.ShouldBeFalse();
    }

    [Theory]
    [InlineData((DatabaseDialect)0)]
    [InlineData((DatabaseDialect)99)]
    public async Task Unknown_dialect_still_throws(DatabaseDialect dialect)
    {
        var sut = new DbSessionContextInitializer();
        var backend = new PooledBackendState();
        await using var conn = backend.RentConnection();

        await Should.ThrowAsync<NotSupportedException>(() =>
            sut.InitializeSessionAsync(conn, dialect, new TenantId(Tenant), AccessMode.ReadOnly));
        await Should.ThrowAsync<NotSupportedException>(() =>
            sut.InitializeSessionAsync(conn, "mysql", new TenantId(Tenant), AccessMode.ReadOnly));
    }

    private sealed class SimulatedReadOnlyTransactionViolationException(string message) : DbException(message);

    private sealed record ExecutedCommand(string Sql, bool HasTransaction);

    private sealed class PooledBackendState
    {
        public bool FailOnSetTransactionReadOnly { get; init; }
        public bool SessionLevelReadOnlyPoisoned { get; set; }
        public bool HasDanglingActiveTransaction { get; set; }
        public int ReadOnlyViolationCount { get; set; }
        public PooledTransaction? CurrentTransaction { get; set; }
        public List<PooledRental> Rentals { get; } = [];

        public PooledDbConnection RentConnection()
        {
            var rental = new PooledRental(this);
            Rentals.Add(rental);
            return new PooledDbConnection(this, rental);
        }
    }

    private sealed class PooledRental(PooledBackendState backend)
    {
        public List<ExecutedCommand> Commands { get; } = [];
        public List<PooledTransaction> Transactions { get; } = [];
        public bool Closed { get; set; }
        public bool RollbackHappenedBeforeConnectionClose { get; set; }

        public void RecordCommand(string sql, DbTransaction? tx)
        {
            Commands.Add(new ExecutedCommand(sql, tx != null));
            if (sql.Contains("SET SESSION CHARACTERISTICS", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains("default_transaction_read_only", StringComparison.OrdinalIgnoreCase))
            {
                backend.SessionLevelReadOnlyPoisoned = true;
            }
        }
    }

    private sealed class PooledDbConnection(PooledBackendState backend, PooledRental rental) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close()
        {
            rental.Closed = true;
            if (backend.CurrentTransaction is { Completed: false })
            {
                backend.HasDanglingActiveTransaction = true;
            }
            _state = ConnectionState.Closed;
        }

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            var tx = new PooledTransaction(this, backend, rental, backend.SessionLevelReadOnlyPoisoned);
            backend.CurrentTransaction = tx;
            rental.Transactions.Add(tx);
            return tx;
        }

        protected override DbCommand CreateDbCommand() => new PooledDbCommand(this, backend, rental);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Close();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class PooledTransaction(
        PooledDbConnection connection,
        PooledBackendState backend,
        PooledRental rental,
        bool isReadOnly) : DbTransaction
    {
        public bool IsReadOnly { get; set; } = isReadOnly;
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public bool Completed { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            CommitCount++;
            Completed = true;
            if (ReferenceEquals(backend.CurrentTransaction, this))
            {
                backend.CurrentTransaction = null;
            }
        }

        public override void Rollback()
        {
            RollbackCount++;
            Completed = true;
            if (!rental.Closed)
            {
                rental.RollbackHappenedBeforeConnectionClose = true;
            }
            if (ReferenceEquals(backend.CurrentTransaction, this))
            {
                backend.CurrentTransaction = null;
            }
        }
    }

    private sealed class PooledDbCommand(
        PooledDbConnection connection,
        PooledBackendState backend,
        PooledRental rental) : DbCommand
    {
        private readonly PooledDbParameterCollection _parameters = new();

        public bool BindByName { get; set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override void Prepare() { }

        public override int ExecuteNonQuery()
        {
            rental.RecordCommand(CommandText, DbTransaction);

            if (string.Equals(CommandText, "SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase))
            {
                if (backend.FailOnSetTransactionReadOnly)
                {
                    throw new InvalidOperationException("Failed to enter read-only transaction mode.");
                }

                if (DbTransaction is not PooledTransaction ptx)
                {
                    throw new InvalidOperationException("SET TRANSACTION READ ONLY requires an active transaction.");
                }

                ptx.IsReadOnly = true;
                return 0;
            }

            if (CommandText.TrimStart().StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
                DbTransaction is PooledTransaction { IsReadOnly: true })
            {
                backend.ReadOnlyViolationCount++;
                throw new SimulatedReadOnlyTransactionViolationException("cannot execute UPDATE in a read-only transaction");
            }

            return 1;
        }

        public override object? ExecuteScalar() => null;

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            rental.RecordCommand(CommandText, DbTransaction);

            if (DbTransaction is PooledTransaction { IsReadOnly: true } &&
                CommandText.Contains("nextval(", StringComparison.OrdinalIgnoreCase))
            {
                backend.ReadOnlyViolationCount++;
                throw new SimulatedReadOnlyTransactionViolationException("cannot execute nextval() in a read-only transaction");
            }

            return new SingleColumnDataReader();
        }

        protected override DbParameter CreateDbParameter() => new PooledDbParameter();
    }

    private sealed class PooledDbParameter : DbParameter
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

    private sealed class PooledDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => ((System.Collections.ICollection)_items).SyncRoot;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class SingleColumnDataReader : DbDataReader
    {
        public override int FieldCount => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => 1;
        public override object this[string name] => 1;
        public override bool GetBoolean(int ordinal) => true;
        public override byte GetByte(int ordinal) => 1;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '1';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "int";
        public override DateTime GetDateTime(int ordinal) => DateTime.UtcNow;
        public override decimal GetDecimal(int ordinal) => 1m;
        public override double GetDouble(int ordinal) => 1d;
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override Type GetFieldType(int ordinal) => typeof(int);
        public override float GetFloat(int ordinal) => 1f;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 1;
        public override int GetInt32(int ordinal) => 1;
        public override long GetInt64(int ordinal) => 1L;
        public override string GetName(int ordinal) => "id";
        public override int GetOrdinal(string name) => 0;
        public override string GetString(int ordinal) => "1";
        public override object GetValue(int ordinal) => 1;
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => false;
        public override bool NextResult() => false;
        public override bool Read() => false;
    }
}
