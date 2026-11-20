using System.Runtime.CompilerServices;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

public sealed class ShapeReadRetryPolicyTests
{
    private const string SampleStatement = "SELECT c.name FROM sys.indexes i WHERE i.object_id = @o";

    private sealed class SyntheticSqlFailure(int number, int lineNumber, string message = "Incorrect syntax near 'sensitive-value'.")
        : Exception(message)
    {
        public int Number { get; } = number;
        public int LineNumber { get; } = lineNumber;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> Properties)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> kvps
                ? kvps.ToArray()
                : [];
            Entries.Add((logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return new ImmediateAsyncTimer(callback, state);
        }

        private sealed class ImmediateAsyncTimer : ITimer
        {
            private int _disposed;

            public ImmediateAsyncTimer(TimerCallback callback, object? state)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static tuple =>
                    {
                        var (timer, cb, st) = tuple;
                        if (Volatile.Read(ref timer._disposed) == 0)
                        {
                            cb(st);
                        }
                    },
                    (this, callback, state),
                    preferLocal: false);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static (int Number, int LineNumber)? ExtractSynthetic(Exception ex) =>
        ex is SyntheticSqlFailure sql ? (sql.Number, sql.LineNumber) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> ThrowSyntheticAtOrigin(SyntheticSqlFailure ex) => throw ex;

    [Fact]
    public async Task Error102_RetriesThenSucceeds()
    {
        var attempts = 0;
        var usables = 0;
        var time = new RecordingTimeProvider();

        var result = await ShapeReadRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new SyntheticSqlFailure(102, 1);
                }

                return Task.FromResult(42);
            },
            _ =>
            {
                usables++;
                return Task.FromResult(true);
            },
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time);

        result.ShouldBe(42);
        attempts.ShouldBe(3);
        usables.ShouldBe(2);
        time.Delays.ShouldBe([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)]);
    }

    [Fact]
    public async Task Error102_StopsAfterThreeAttempts_RethrowsOriginal()
    {
        var attempts = 0;
        var time = new RecordingTimeProvider();
        var finalException = new SyntheticSqlFailure(102, 1);

        var thrown = await Should.ThrowAsync<SyntheticSqlFailure>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                attempts++;
                return ThrowSyntheticAtOrigin(attempts == 3 ? finalException : new SyntheticSqlFailure(102, 1));
            },
            _ => Task.FromResult(true),
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time));

        attempts.ShouldBe(3);
        thrown.ShouldBeSameAs(finalException);
        thrown.StackTrace.ShouldNotBeNull();
        thrown.StackTrace.ShouldContain(nameof(ThrowSyntheticAtOrigin));
        time.Delays.ShouldBe([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)]);
    }

    [Fact]
    public async Task OtherSqlError_NoRetry()
    {
        var attempts = 0;
        var usables = 0;
        var time = new RecordingTimeProvider();
        var original = new SyntheticSqlFailure(208, 1);

        var thrown = await Should.ThrowAsync<SyntheticSqlFailure>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                attempts++;
                throw original;
            },
            _ =>
            {
                usables++;
                return Task.FromResult(true);
            },
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time));

        thrown.ShouldBeSameAs(original);
        attempts.ShouldBe(1);
        usables.ShouldBe(0);
        time.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonSqlException_NoRetry()
    {
        var attempts = 0;
        var usables = 0;
        var time = new RecordingTimeProvider();
        var original = new InvalidOperationException("connection closed");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                attempts++;
                throw original;
            },
            _ =>
            {
                usables++;
                return Task.FromResult(true);
            },
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time));

        thrown.ShouldBeSameAs(original);
        attempts.ShouldBe(1);
        usables.ShouldBe(0);
        time.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransactionNotUsable_NoRetry()
    {
        var attempts = 0;
        var usables = 0;
        var time = new RecordingTimeProvider();
        var original = new SyntheticSqlFailure(102, 1);

        var thrown = await Should.ThrowAsync<SyntheticSqlFailure>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                attempts++;
                return ThrowSyntheticAtOrigin(original);
            },
            _ =>
            {
                usables++;
                return Task.FromResult(false);
            },
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time));

        thrown.ShouldBeSameAs(original);
        thrown.StackTrace.ShouldNotBeNull();
        thrown.StackTrace.ShouldContain(nameof(ThrowSyntheticAtOrigin));
        attempts.ShouldBe(1);
        usables.ShouldBe(1);
        time.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelled_NoRetry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempts = 0;
        var usables = 0;
        var time = new RecordingTimeProvider();
        var original = new SyntheticSqlFailure(102, 1);

        var thrown = await Should.ThrowAsync<SyntheticSqlFailure>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                attempts++;
                return ThrowSyntheticAtOrigin(original);
            },
            _ =>
            {
                usables++;
                return Task.FromResult(true);
            },
            NullLogger.Instance,
            SampleStatement,
            cts.Token,
            ExtractSynthetic,
            time));

        thrown.ShouldBeSameAs(original);
        attempts.ShouldBe(1);
        usables.ShouldBe(0);
        time.Delays.ShouldBeEmpty();

        using var midCallCts = new CancellationTokenSource();
        var midAttempts = 0;
        await Should.ThrowAsync<SyntheticSqlFailure>(() => ShapeReadRetryPolicy.RunAsync<int>(
            _ =>
            {
                midAttempts++;
                midCallCts.Cancel();
                return ThrowSyntheticAtOrigin(new SyntheticSqlFailure(102, 1));
            },
            _ => Task.FromResult(true),
            NullLogger.Instance,
            SampleStatement,
            midCallCts.Token,
            ExtractSynthetic,
            new RecordingTimeProvider()));

        midAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Retry_LogsStatementAndSqlClientVersion()
    {
        var logger = new RecordingLogger();
        var time = new RecordingTimeProvider();
        var attempts = 0;
        const string serverSecret = "secret-server-value-never-logged";

        var result = await ShapeReadRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new SyntheticSqlFailure(102, 4, $"Incorrect syntax near '{serverSecret}'.");
                }

                return Task.FromResult("ok");
            },
            _ => Task.FromResult(true),
            logger,
            SampleStatement,
            CancellationToken.None,
            ExtractSynthetic,
            time);

        result.ShouldBe("ok");
        logger.Entries.Count.ShouldBe(2);
        logger.Entries.ShouldAllBe(e => e.Level == LogLevel.Warning);
        logger.Entries.ShouldAllBe(e => e.Exception == null);
        logger.Entries.ShouldAllBe(e => e.Message.Contains(SampleStatement, StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => e.Message.Contains(ShapeReadRetryPolicy.SqlClientVersion, StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => e.Message.Contains("102", StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => e.Message.Contains("line 4", StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => !e.Message.Contains(serverSecret, StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(e => e.Properties.Any(kvp => kvp.Key == "ErrorNumber" && Equals(kvp.Value, 102)));
        logger.Entries.ShouldAllBe(e => e.Properties.Any(kvp => kvp.Key == "LineNumber" && Equals(kvp.Value, 4)));
        logger.Entries[0].Properties.ShouldContain(kvp => kvp.Key == "Attempt" && Equals(kvp.Value, 1));
        logger.Entries[1].Properties.ShouldContain(kvp => kvp.Key == "Attempt" && Equals(kvp.Value, 2));
    }

    [RuntimeSkippableFact]
    public async Task DefaultSqlExceptionExtractor_RetriesRealError102()
    {
        var realSql102 = MigrationFailureDiagnosticsTests.TryCreateSqlException(102, lineNumber: 1)
            ?? throw new RuntimeSkipException($"SqlClient {ShapeReadRetryPolicy.SqlClientVersion} changed its internal SqlError API");

        var attempts = 0;
        var time = new RecordingTimeProvider();

        var result = await ShapeReadRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw realSql102;
                }

                return Task.FromResult("recovered");
            },
            _ => Task.FromResult(true),
            NullLogger.Instance,
            SampleStatement,
            CancellationToken.None,
            extractSqlError: null,
            timeProvider: time);

        result.ShouldBe("recovered");
        attempts.ShouldBe(2);
        time.Delays.ShouldBe([TimeSpan.FromMilliseconds(50)]);
    }

    [RuntimeSkippableFact]
    public async Task SqlServerSchemaCatalogReader_ListTablesAsync_RetriesError102_WhenXactStateIsOne()
    {
        var sql102 = MigrationFailureDiagnosticsTests.TryCreateSqlException(102, lineNumber: 1)
            ?? throw new RuntimeSkipException($"SqlClient {ShapeReadRetryPolicy.SqlClientVersion} changed its internal SqlError API");

        var catalogAttempts = 0;
        var xactChecks = 0;
        var logger = new RecordingLogger();

        await using var conn = new CallbackDbConnection((sql, _) =>
        {
            if (sql.Contains("XACT_STATE()", StringComparison.Ordinal))
            {
                xactChecks++;
                return new StubDbDataReader(["state"], [[1]]);
            }

            catalogAttempts++;
            if (catalogAttempts == 1)
            {
                throw sql102;
            }

            return new StubDbDataReader(["name"], [["TABLES"]]);
        });
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var reader = new SqlServerSchemaCatalogReader(logger);
        var tables = await reader.ListTablesAsync(conn, CancellationToken.None, tx);

        tables.ShouldBe(["TABLES"]);
        catalogAttempts.ShouldBe(2);
        xactChecks.ShouldBe(1);
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Level.ShouldBe(LogLevel.Warning);
        logger.Entries[0].Message.ShouldContain("sys.tables");
    }

    [RuntimeSkippableFact]
    public async Task SqlServerSchemaCatalogReader_ReadAsync_DoesNotRetryError102_WhenXactStateIsDoomed()
    {
        var sql102 = MigrationFailureDiagnosticsTests.TryCreateSqlException(102, lineNumber: 1)
            ?? throw new RuntimeSkipException($"SqlClient {ShapeReadRetryPolicy.SqlClientVersion} changed its internal SqlError API");

        var catalogAttempts = 0;
        var xactChecks = 0;
        var logger = new RecordingLogger();

        await using var conn = new CallbackDbConnection((sql, _) =>
        {
            if (sql.Contains("XACT_STATE()", StringComparison.Ordinal))
            {
                xactChecks++;
                return new StubDbDataReader(["state"], [[-1]]);
            }

            catalogAttempts++;
            throw sql102;
        });
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var reader = new SqlServerSchemaCatalogReader(logger);
        var thrown = await Should.ThrowAsync<Microsoft.Data.SqlClient.SqlException>(
            () => reader.ReadAsync(conn, ["TABLES"], CancellationToken.None, tx));

        thrown.ShouldBeSameAs(sql102);
        thrown.Data.Contains(SqlMigrationStep.FailedStatementDataKey).ShouldBeTrue();
        catalogAttempts.ShouldBe(1);
        xactChecks.ShouldBe(1);
        logger.Entries.ShouldBeEmpty();
    }
}
