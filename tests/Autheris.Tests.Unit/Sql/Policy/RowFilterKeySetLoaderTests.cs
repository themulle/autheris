namespace Autheris.Tests.Unit.Sql.Policy;

using System.Collections.Immutable;
using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Xunit;

/// <summary>
/// Issue #107 loader: SEC-RF107-01 b (tenant-only session), SEC-RF107-08 (stable denial, no names, nothing partial), SEC-RF107-12 (isolation and
/// read-only transactions), SEC-RF107-20 and D-WPC-8 (every bind, parameter count), SEC-RF107-13 c (at most MaxKeys + 1 rows).
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetLoaderTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private readonly IDbSessionContextInitializer _session = Substitute.For<IDbSessionContextInitializer>();

    private RowFilterKeySetLoader Loader(DbConnection? connection, Exception? factoryFailure = null, IDbSessionContextInitializer? session = null)
        => new(new FixedConnectionFactory(connection, factoryFailure), session ?? _session, _time, new ListLogger<RowFilterKeySetLoader>(_logs));

    private static CompiledKeyQuery Query(params BoundParameter[] extra) => new(
        "SELECT DISTINCT dep.pk FROM dep WHERE tenant = :p1",
        [new BoundParameter(":p1", "p1", 1, "tenant-a", SqlParameterType.String, ParameterOrigin.Tenant), .. extra],
        "int", "int", "fk", "v1");

    private static RowFilterKeySetLoadRequest Request(
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        string provider = "SqlServer",
        CompiledKeyQuery? query = null,
        int maxKeys = 100,
        TimeSpan? timeout = null,
        string fkType = "int",
        bool readUncommitted = false,
        string keyType = "int")
        => new(
            new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x", ReadUncommitted = readUncommitted },
            dialect,
            Tenant,
            "ds",
            query ?? Query(),
            KeySetValueConverter.Create(dialect, keyType, fkType)!,
            maxKeys,
            timeout ?? TimeSpan.FromSeconds(5));

    private static RecordingDbConnection Db(params object?[] rows) => new() { KeyRows = [.. rows] };

    private static string WholeType(TargetSqlDialect d) => d == TargetSqlDialect.Oracle ? "number(10)" : "integer";

    private static string Provider(TargetSqlDialect d) => d switch
    {
        TargetSqlDialect.PostgreSql => "PostgreSql",
        TargetSqlDialect.Oracle => "Oracle",
        TargetSqlDialect.Sqlite => "Sqlite",
        _ => "SqlServer"
    };

    [Fact]
    public async Task LoadsConvertedKeys_AndTakesLoadedAtBeforeTheQuery_SEC_RF107_04_b()
    {
        var db = Db(1, 2L, 3);
        // time passes while the session is initialised: the entry still carries the instant before the key query started
        _session.When(s => s.InitializeSessionAsync(Arg.Any<DbConnection>(), Arg.Any<DbTransaction?>(), Arg.Any<DatabaseDialect>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(_ => _time.Advance(TimeSpan.FromSeconds(30)));
        var start = _time.GetUtcNow();

        var entry = await Loader(db).LoadAsync(Request(), CancellationToken.None);

        entry.Keys.ShouldBe([1L, 2L, 3L]);
        entry.Overflow.ShouldBeFalse();
        entry.NotBindable.ShouldBeFalse();
        entry.LoadedAt.ShouldBe(start);
        entry.Tenant.ShouldBe("tenant-a");
        entry.DataSource.ShouldBe("ds");
        db.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ClientCancellation_IsRethrown_NotMappedToUnavailable_SEC_RF107_08()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Loader(Db(1), factoryFailure: new OperationCanceledException(cts.Token)).LoadAsync(Request(), cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
        _logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task OperationCanceled_WithoutClientCancellation_StaysUnavailable_SEC_RF107_08()
    {
        var act = () => Loader(Db(1), factoryFailure: new OperationCanceledException()).LoadAsync(Request(), CancellationToken.None);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(act);
        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
    }

    [Fact]
    public async Task SqlServer_ForcesReadCommitted_EvenWithReadUncommitted_SEC_RF107_12()
    {
        var db = Db(1);

        await Loader(db).LoadAsync(Request(readUncommitted: true), CancellationToken.None);

        db.Commands.Select(c => c.Text).ShouldBe(["SET TRANSACTION ISOLATION LEVEL READ COMMITTED", "SELECT DISTINCT dep.pk FROM dep WHERE tenant = :p1"]);
        db.Transactions.ShouldBeEmpty();
        db.Commands.ShouldAllBe(c => !c.InTransaction);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    public async Task PostgreSqlAndOracle_ReadOnlyTransaction_RolledBack_SEC_RF107_12(TargetSqlDialect dialect)
    {
        var db = Db(1, 2);

        await Loader(db).LoadAsync(Request(dialect, Provider(dialect), fkType: WholeType(dialect), keyType: WholeType(dialect)), CancellationToken.None);

        db.Commands[0].Text.ShouldBe("SET TRANSACTION READ ONLY");
        db.Commands.ShouldAllBe(c => c.InTransaction);
        db.Commands.Select(c => c.Text).ShouldContain("SELECT DISTINCT dep.pk FROM dep WHERE tenant = :p1");
        var tx = db.Transactions.ShouldHaveSingleItem();
        tx.RolledBack.ShouldBeTrue();
        tx.Committed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    public async Task ReadOnlyTransaction_IsRolledBack_AlsoWhenTheQueryFails_SEC_RF107_12(TargetSqlDialect dialect)
    {
        var db = new RecordingDbConnection { ExecuteFailure = new InvalidOperationException("boom") };

        await Should.ThrowAsync<GatewaySecurityException>(() =>
            Loader(db).LoadAsync(Request(dialect, Provider(dialect), fkType: WholeType(dialect), keyType: WholeType(dialect)), CancellationToken.None));

        db.Transactions.ShouldHaveSingleItem().RolledBack.ShouldBeTrue();
    }

    [Fact]
    public async Task SessionIsTenantOnly_NoUserSidNoPurpose_SEC_RF107_01_b()
    {
        var db = Db(1);

        await Loader(db).LoadAsync(Request(TargetSqlDialect.PostgreSql, "PostgreSql", fkType: "integer", keyType: "integer"), CancellationToken.None);

        await _session.Received(1).InitializeSessionAsync(
            Arg.Any<DbConnection>(), Arg.Any<DbTransaction?>(), DatabaseDialect.PostgreSql, Tenant,
            Arg.Is<string?>(s => s == null), Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RealSessionInitializer_BindsAnEmptyUserSid_OnPostgreSql_SEC_RF107_01_b()
    {
        var db = Db(1);

        await Loader(db, session: new DbSessionContextInitializer()).LoadAsync(Request(TargetSqlDialect.PostgreSql, "PostgreSql", fkType: "integer", keyType: "integer"), CancellationToken.None);

        var init = db.Commands.Single(c => c.Text.Contains("autheris.user_sid", StringComparison.Ordinal));
        init.Parameters.Single(p => p.Name == "@sid").Value.ShouldBe(string.Empty);
        init.Parameters.Single(p => p.Name == "@purpose").Value.ShouldBe(string.Empty);
        init.Parameters.Single(p => p.Name == "@tenant").Value.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task BindsEveryCompiledParameter_ByName_InArrayOrder_D_WPC_8()
    {
        var db = Db(1);
        var query = Query(new BoundParameter(":p2", "p2", 2, 7L, SqlParameterType.Int64, ParameterOrigin.Policy),
            new BoundParameter(":p3", "p3", 3, 101L, SqlParameterType.Int64, ParameterOrigin.QueryLiteral));

        await Loader(db).LoadAsync(Request(query: query), CancellationToken.None);

        var select = db.Commands.Last();
        select.Parameters.Select(p => p.Name).ShouldBe(["p1", "p2", "p3"]);
        select.Parameters.Select(p => p.Value).ShouldBe(["tenant-a", 7L, 101L]);
    }

    [Fact]
    public async Task ParameterCountMismatch_Unavailable_QueryNotExecuted_SEC_RF107_20()
    {
        var db = new RecordingDbConnection { KeyRows = [1], DropParameters = true };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(db).LoadAsync(Request(), CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
        db.Commands.ShouldNotContain(c => c.Text.StartsWith("SELECT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sqlite_BindsRowLimitParameter_AndAllCompiledParameters_D_WPC_8()
    {
        // a shared in-memory database stays alive while this keep-alive connection is open; the loader opens (and disposes) its own
        string connectionString = $"Data Source=file:keyset{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var sqlite = new SqliteConnection(connectionString);
        await sqlite.OpenAsync();
        await using (var setup = sqlite.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE dep (pk INTEGER, tenant TEXT); INSERT INTO dep VALUES (1,'tenant-a'),(2,'tenant-a'),(3,'tenant-a'),(4,'tenant-a'),(5,'tenant-b');";
            await setup.ExecuteNonQueryAsync();
        }

        var query = new CompiledKeyQuery(
            "SELECT DISTINCT pk FROM dep WHERE tenant = ?1 AND pk IS NOT NULL ORDER BY pk LIMIT ?2",
            [new BoundParameter("?1", "p1", 1, "tenant-a", SqlParameterType.String, ParameterOrigin.Tenant),
             new BoundParameter("?2", "p2", 2, 4L, SqlParameterType.Int64, ParameterOrigin.QueryLiteral)],
            "integer", "integer", "fk", "v1");
        var request = Request(TargetSqlDialect.Sqlite, "Sqlite", query, maxKeys: 3, fkType: "integer", keyType: "integer");

        // MaxKeys + 1 = 4 rows exist for the tenant: that is an overflow
        var sqliteLoader = new RowFilterKeySetLoader(new SqliteFactory(connectionString), _session, _time);
        var overflow = await sqliteLoader.LoadAsync(request, CancellationToken.None);
        overflow.Overflow.ShouldBeTrue();
        overflow.Keys.ShouldBeEmpty();

        var all = await sqliteLoader.LoadAsync(request with { MaxKeys = 4 }, CancellationToken.None);
        all.Keys.ShouldBe([1L, 2L, 3L, 4L]);

        // the session is not left read-only: the loader's own connection is disposed, but a write through a fresh one must work
        await using var write = sqlite.CreateCommand();
        write.CommandText = "INSERT INTO dep VALUES (9, 'tenant-a')";
        (await write.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Sqlite_SessionIsQueryOnly_WhileTheKeyQueryRuns_SEC_RF107_12()
    {
        var db = new RecordingDbConnection { KeyRows = [1] };

        await Loader(db).LoadAsync(Request(TargetSqlDialect.Sqlite, "Sqlite", fkType: "integer", keyType: "integer"), CancellationToken.None);

        db.Commands.Select(c => c.Text).ShouldBe(["PRAGMA query_only = ON", "SELECT DISTINCT dep.pk FROM dep WHERE tenant = :p1", "PRAGMA query_only = OFF"]);
    }

    [Fact]
    public async Task ReadsAtMostMaxKeysPlusOneRows_AndReportsOverflow_SEC_RF107_13_c()
    {
        var db = Db(Enumerable.Range(1, 1000).Cast<object?>().ToArray());

        var entry = await Loader(db).LoadAsync(Request(maxKeys: 3), CancellationToken.None);

        entry.Overflow.ShouldBeTrue();
        entry.Keys.ShouldBeEmpty();
        db.RowsRead.ShouldBe(4);
        db.Cancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task ExactlyMaxKeysRows_IsNotAnOverflow()
    {
        var entry = await Loader(Db(1, 2, 3)).LoadAsync(Request(maxKeys: 3), CancellationToken.None);

        entry.Overflow.ShouldBeFalse();
        entry.Keys.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ValueThatDoesNotFitTheForeignKey_YieldsTheNotBindableSentinel_NothingDropped_SEC_RF107_07_b()
    {
        var entry = await Loader(Db(1, 300)).LoadAsync(Request(fkType: "tinyint", keyType: "tinyint"), CancellationToken.None);

        entry.NotBindable.ShouldBeTrue();
        entry.Keys.ShouldBeEmpty();
        entry.Overflow.ShouldBeFalse();
    }

    [Fact]
    public async Task NullKey_IsNotDropped_YieldsTheNotBindableSentinel()
        => (await Loader(Db(1, null)).LoadAsync(Request(), CancellationToken.None)).NotBindable.ShouldBeTrue();

    [Fact]
    public async Task EmptyResult_IsAnEmptyKeySet()
    {
        var entry = await Loader(Db()).LoadAsync(Request(), CancellationToken.None);

        entry.Keys.ShouldBeEmpty();
        entry.Overflow.ShouldBeFalse();
        entry.NotBindable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(5, 5)]
    [InlineData(2.5, 3)]
    public async Task CommandTimeout_IsTheKeyQueryTimeout_RoundedUp(double seconds, int expected)
    {
        var db = Db(1);

        await Loader(db).LoadAsync(Request(timeout: TimeSpan.FromSeconds(seconds)), CancellationToken.None);

        db.Commands.Last().Timeout.ShouldBe(expected);
    }

    // ---- SEC-RF107-08 ----

    [Fact]
    public async Task ConnectionFailure_IsTheStableDenial_NoNamesInMessage_InnerAttached_SEC_RF107_08()
    {
        var cause = new InvalidOperationException("login failed for table secret_dep_table of rule 4711");

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(null, cause).LoadAsync(Request(), CancellationToken.None));

        ex.ErrorCode.ShouldBe("ROW_FILTER_KEY_SET_UNAVAILABLE");
        ex.Message.ShouldBe(RowFilterKeySetErrorCodes.UnavailableMessage);
        ex.Message.ShouldNotContain("secret_dep_table");
        ex.Message.ShouldNotContain("dep");
        ex.Message.ShouldNotContain("4711");
        ex.InnerException.ShouldBeSameAs(cause);
        _logs.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        _logs.ShouldAllBe(l => !l.Message.Contains("secret_dep_table", StringComparison.Ordinal), "the Warning carries the exception type only");
    }

    [Fact]
    public async Task CommandFailure_IsTheStableDenial_SEC_RF107_08()
    {
        var db = new RecordingDbConnection { ExecuteFailure = new TimeoutException("Execution Timeout Expired on dbo.secret") };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(db).LoadAsync(Request(), CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
        ex.Message.ShouldNotContain("secret");
        ex.InnerException.ShouldBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task ReadFailureAfterSomeRows_DiscardsThePartialRows_SEC_RF107_08()
    {
        var db = new RecordingDbConnection { KeyRows = [1, 2, 3, 4], FailAfterRows = 2 };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(db).LoadAsync(Request(), CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
        db.RowsRead.ShouldBe(2);   // two rows were read, and none of them reaches a result
    }

    [Fact]
    public async Task SessionInitializationFailure_IsTheStableDenial_SEC_RF107_08()
    {
        _session.InitializeSessionAsync(Arg.Any<DbConnection>(), Arg.Any<DbTransaction?>(), Arg.Any<DatabaseDialect>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("no session")));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(Db(1)).LoadAsync(Request(), CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
    }

    [Fact]
    public async Task ProviderThatDoesNotMatchTheDialect_IsUnavailable_NoConnectionUsed()
    {
        var factory = new FixedConnectionFactory(Db(1));
        var loader = new RowFilterKeySetLoader(factory, _session, _time);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            loader.LoadAsync(Request(TargetSqlDialect.SqlServer, "PostgreSql"), CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
        factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task DialectWithoutLoader_IsUnavailable()
    {
        var request = Request(TargetSqlDialect.SqlServer) with { Dialect = TargetSqlDialect.DuckDb };

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => Loader(Db(1)).LoadAsync(request, CancellationToken.None));

        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
    }

    private sealed class SqliteFactory(string connectionString) : ISqlConnectionFactory
    {
        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }
    }
}
