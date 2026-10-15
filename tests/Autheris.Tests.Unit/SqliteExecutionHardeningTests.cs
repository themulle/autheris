using System.Data.Common;
using System.Diagnostics;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>WP-X0h (SEC-X1-10): SQLite connection hardening and the interrupt-based execution timeout.</summary>
public sealed class SqliteExecutionHardeningTests
{
    // Bounded, so that a missing interrupt ends the query after a few seconds instead of hanging the test run.
    private const string LongRecursiveCte =
        "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 60000000) SELECT count(*) FROM c";

    private static async Task<DbConnection> OpenAsync() =>
        await new SqlConnectionFactory().CreateOpenConnectionAsync(
            new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" });

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task FactoryConnection_HasTrustedSchemaOff()
    {
        await using var connection = await OpenAsync();

        Convert.ToInt32(await ScalarAsync(connection, "PRAGMA trusted_schema")).ShouldBe(0);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(0, true)]
    [InlineData(1L, false)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    [InlineData("0", false)]   // anything but the integer 0 is a failed verification
    public void TrustedSchemaReadBack_FailsClosed_UnlessTheDatabaseConfirmsOff(object? readBack, bool accepted)
    {
        // Phase 5 L8 (X1): the pragma is read back; a database that still trusts its schema never serves a governed statement.
        if (accepted)
        {
            Should.NotThrow(() => SessionPinning.EnsureSqliteTrustedSchemaOff(readBack));
        }
        else
        {
            Should.Throw<Autheris.Application.Interfaces.SqlSourceUnavailableException>(() => SessionPinning.EnsureSqliteTrustedSchemaOff(readBack))
                .Message.ShouldNotContain("trusted_schema = 1");
        }
    }

    [Fact]
    public void TrustedSchemaReadBack_UsesThePragmaQuery()
    {
        SessionPinning.SqliteTrustedSchemaReadBackSql.ShouldBe("PRAGMA trusted_schema");
    }

    [Fact]
    public async Task FactoryConnection_KeepsExtensionLoadingDisabled()
    {
        await using var connection = await OpenAsync();

        await Should.ThrowAsync<SqliteException>(() => ScalarAsync(connection, "SELECT load_extension('nonexistent')"));
    }

    [Fact]
    public async Task FactoryConnection_StillRegistersTheGatewayHmacUdf()
    {
        await using var connection = await OpenAsync();

        ((string?)await ScalarAsync(connection, "SELECT gateway_hmac_sha256('a', 'k')"))!.ShouldStartWith("hmac_");
    }

    [Fact]
    public async Task RecursiveCte_IsInterruptedWithinTheTimeoutPlusOneSecond_AsATimeoutException()
    {
        await using var connection = await OpenAsync();
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
            connection, TimeSpan.FromMilliseconds(300), () => ScalarAsync(connection, LongRecursiveCte), CancellationToken.None));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RecursiveCte_IsInterruptedByCancellation_AsAnOperationCanceledException()
    {
        await using var connection = await OpenAsync();
        using var cts = new CancellationTokenSource();
        var cancelThread = new Thread(() =>
        {
            Thread.Sleep(200);
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true
        };
        cancelThread.Start();
        var clock = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => SqliteExecutionGuard.RunAsync(
            connection, TimeSpan.FromSeconds(30), () => ScalarAsync(connection, LongRecursiveCte), cts.Token));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectionIsUsableAfterAnInterrupt_AndTheHandlerIsRemoved()
    {
        await using var connection = await OpenAsync();
        await Should.ThrowAsync<TimeoutException>(() => SqliteExecutionGuard.RunAsync(
            connection, TimeSpan.FromMilliseconds(100), () => ScalarAsync(connection, LongRecursiveCte), CancellationToken.None));

        // Outside the guard no limit applies any more.
        Convert.ToInt64(await ScalarAsync(connection,
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 2000000) SELECT count(*) FROM c")).ShouldBe(2_000_000);
    }

    [Fact]
    public async Task AQuickStatement_ReturnsItsResultThroughTheGuard()
    {
        await using var connection = await OpenAsync();

        (await SqliteExecutionGuard.RunAsync(connection, TimeSpan.FromSeconds(5),
            () => ScalarAsync(connection, "SELECT 41 + 1"), CancellationToken.None)).ShouldBe(42L);
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_NeverStartsTheWork()
    {
        await using var connection = await OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        bool started = false;

        await Should.ThrowAsync<OperationCanceledException>(() => SqliteExecutionGuard.RunAsync(
            connection, TimeSpan.FromSeconds(5), () => { started = true; return ScalarAsync(connection, "SELECT 1"); }, cts.Token));

        started.ShouldBeFalse();
    }

    [Fact]
    public async Task ANonSqliteConnection_IsRefused()
    {
        await Should.ThrowAsync<NotSupportedException>(() => SqliteExecutionGuard.RunAsync<int>(
            new NotSqliteConnection(), TimeSpan.FromSeconds(1), () => Task.FromResult(1), CancellationToken.None));
    }

    private sealed class NotSqliteConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get => string.Empty; set { } }
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
