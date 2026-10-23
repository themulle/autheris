using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>Issue #286: the flush worker reports a failing store without flooding the log and never hides a persistent failure.</summary>
public sealed class ConsumerUsageFlushWorkerTests
{
    private const string TenantValue = "tenant-secret-value";
    private const string AppValue = "app-secret-value";
    private const string ActorSid = "S-1-5-21-111-222-333-4444";

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<ConsumerUsageFlushWorker>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception is not null)
            {
                message += " | exception: " + exception;
            }

            Entries.Add((logLevel, message));
        }

        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
    }

    private sealed record Harness(
        ConsumerUsageFlushWorker Worker,
        ConsumerUsageRecorder Recorder,
        IConsumerAggregationRepository Repo,
        CapturingLogger Logger,
        MutableClock Clock);

    private static Harness Create(bool failing = true)
    {
        var repo = Substitute.For<IConsumerAggregationRepository>();
        if (failing)
        {
            var failure = new InvalidOperationException($"no such table: CONSUMER_APP_DAILY (tenant {TenantValue}, app {AppValue})");
            repo.RecordDailyUsageAsync(
                Arg.Any<TableIdentifier>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        }

        var services = new ServiceCollection().AddSingleton(repo).BuildServiceProvider();
        var recorder = new ConsumerUsageRecorder(clusterSecret: "test-cluster-secret-key-12345");
        var logger = new CapturingLogger();
        var clock = new MutableClock();
        var worker = new ConsumerUsageFlushWorker(recorder, services, Options.Create(new AuditOptions()), logger, null, clock);
        return new Harness(worker, recorder, repo, logger, clock);
    }

    private static void Record(ConsumerUsageRecorder recorder) =>
        recorder.RecordUsage(
            new TableIdentifier("sales", "dbo", "orders"),
            new TenantId(TenantValue),
            new Sid(ActorSid),
            ConsumerOutcome.Allow,
            rowsReturned: 5,
            rowsMeasured: true,
            caller: new CallerContext(AppValue, "1.0", "GetOrders"));

    [Fact]
    public async Task RepeatedFailures_LogErrorOnce_ThenDebugUntilSuccess()
    {
        var h = Create();

        for (int i = 0; i < 5; i++)
        {
            Record(h.Recorder);
            h.Clock.Now = h.Clock.Now.AddSeconds(15);
            await h.Worker.FlushOnceAsync(CancellationToken.None);
        }

        h.Logger.Count(LogLevel.Error).ShouldBe(1);
        h.Logger.Count(LogLevel.Debug).ShouldBe(4);
        h.Worker.ConsecutiveFailureCount.ShouldBe(5);

        // Success resets the latch: the next failure is reported at error level again.
        h.Repo.ClearSubstitute(ClearOptions.ReturnValues);
        await h.Worker.FlushOnceAsync(CancellationToken.None);
        h.Worker.ConsecutiveFailureCount.ShouldBe(0);
        h.Logger.Count(LogLevel.Information).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PersistentFailure_ReEmitsError_AtBoundedInterval()
    {
        var h = Create();

        Record(h.Recorder);
        await h.Worker.FlushOnceAsync(CancellationToken.None);
        h.Logger.Count(LogLevel.Error).ShouldBe(1);

        // Within the interval: no new error.
        h.Clock.Now = h.Clock.Now.AddMinutes(14);
        Record(h.Recorder);
        await h.Worker.FlushOnceAsync(CancellationToken.None);
        h.Logger.Count(LogLevel.Error).ShouldBe(1);

        // After the interval: error again, with the failure count and the first failure time.
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        Record(h.Recorder);
        await h.Worker.FlushOnceAsync(CancellationToken.None);
        h.Logger.Count(LogLevel.Error).ShouldBe(2);
        var last = h.Logger.Entries.Last(e => e.Level == LogLevel.Error).Message;
        last.ShouldContain("Consecutive failures: 3");
        last.ShouldContain("2026-10-23T08:00:00");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PersistentFailure_ReEmitsError_EveryHundredFailures()
    {
        var h = Create();

        for (int i = 0; i < 100; i++)
        {
            Record(h.Recorder);
            await h.Worker.FlushOnceAsync(CancellationToken.None);
        }

        // Failure 1 and failure 100 (same clock, so the interval alone would not re-emit).
        h.Logger.Count(LogLevel.Error).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FailureLog_DoesNotContainActorOrTenantValues()
    {
        // SEC-286-5: only exception type and the provider message are logged, never the exception object, parameters or actor values.
        var h = Create();
        Record(h.Recorder);

        await h.Worker.FlushOnceAsync(CancellationToken.None);

        var error = h.Logger.Entries.Single(e => e.Level == LogLevel.Error).Message;
        error.ShouldContain("InvalidOperationException");
        error.ShouldNotContain("| exception:");
        error.ShouldNotContain(ActorSid);
        error.ShouldNotContain("StackTrace");
        foreach (var entry in h.Logger.Entries.Where(e => e.Level != LogLevel.Error))
        {
            entry.Message.ShouldNotContain(ActorSid);
            entry.Message.ShouldNotContain(TenantValue);
            entry.Message.ShouldNotContain(AppValue);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task BufferStaysBounded_WhileStoreFails()
    {
        var h = Create();

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 50; i++)
            {
                h.Recorder.RecordUsage(
                    new TableIdentifier("sales", "dbo", "orders"), new TenantId(TenantValue), new Sid($"S-1-5-21-{round}-{i}"),
                    ConsumerOutcome.Allow, 1, true, caller: new CallerContext($"app-{i}", "1.0", "q"));
            }

            await h.Worker.FlushOnceAsync(CancellationToken.None);

            // A failing flush drains the buffer instead of re-queuing: memory never grows with the outage.
            h.Recorder.ActiveSeriesCount.ShouldBe(0);
        }

        h.Worker.ConsecutiveFailureCount.ShouldBe(3);
    }

    [Fact]
    public async Task Flush_WhenCancelled_Propagates()
    {
        var h = Create();
        Record(h.Recorder);
        using var cts = new CancellationTokenSource();
        h.Repo.RecordDailyUsageAsync(
            Arg.Any<TableIdentifier>(), Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<CancellationToken>()).Returns(_ =>
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => h.Worker.FlushOnceAsync(cts.Token));

        h.Worker.ConsecutiveFailureCount.ShouldBe(0);
    }
}
