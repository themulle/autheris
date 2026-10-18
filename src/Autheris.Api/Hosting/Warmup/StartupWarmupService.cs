namespace Autheris.Api.Hosting.Warmup;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// PLAN-OPS-150 (D-150-7, SEC-OPS150-08): runs the best-effort steps sequentially after the host started. Yields first, gives each
/// step the remaining budget through a token plus a hard <c>WaitAsync</c> bound, honours the host stopping token and logs the step
/// name and the exception type only (SEC-OPS150-09).
/// </summary>
public sealed partial class StartupWarmupService(
    IEnumerable<IStartupWarmup> steps,
    StartupWarmupState state,
    IOptions<StartupWarmupOptions> options,
    TimeProvider timeProvider,
    ILogger<StartupWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var opts = options.Value;
        var budget = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        var start = timeProvider.GetTimestamp();
        state.Set(WarmupPhase.Warming);
        if (!opts.RequireForReadiness)
        {
            state.Set(WarmupPhase.Ready);
        }

        foreach (var step in steps)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (opts.Steps.TryGetValue(step.Name, out var stepOptions) && !stepOptions.Enabled)
            {
                StartupWarmupDiagnostics.RecordStep(step.Name, StartupWarmupDiagnostics.Skipped, 0);
                continue;
            }

            var remaining = budget - timeProvider.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                StartupWarmupDiagnostics.RecordStep(step.Name, StartupWarmupDiagnostics.Skipped, 0);
                LogStepSkipped(logger, step.Name);
                continue;
            }

            await RunStepAsync(step, remaining, stoppingToken).ConfigureAwait(false);
        }

        var total = timeProvider.GetElapsedTime(start);
        StartupWarmupDiagnostics.Duration.Record(total.TotalSeconds);
        state.Set(WarmupPhase.Ready);
        LogFinished(logger, total.TotalMilliseconds);
    }

    private async Task RunStepAsync(IStartupWarmup step, TimeSpan remaining, CancellationToken stoppingToken)
    {
        var stepStart = timeProvider.GetTimestamp();
        string outcome;
        using var budgetCts = new CancellationTokenSource(remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budgetCts.Token);

        Task? task = null;
        try
        {
            // Task.Run so that a step that blocks synchronously is released by the bound as well.
            task = Task.Run(() => step.RunAsync(linked.Token), CancellationToken.None);
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await task.WaitAsync(remaining, timeProvider, stoppingToken).ConfigureAwait(false);
            outcome = StartupWarmupDiagnostics.Completed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (task is not null && ((ex is TimeoutException && !task.IsCompleted) || task.IsCanceled))
        {
            // The bound elapsed (the step is still running) or the step observed the budget token. A step that itself threw a
            // TimeoutException has a completed, faulted task and is a failure below.
            outcome = StartupWarmupDiagnostics.TimedOut;
            LogStepTimedOut(logger, step.Name);
        }
        catch (Exception ex)
        {
            outcome = StartupWarmupDiagnostics.Failed;
            LogStepFailed(logger, step.Name, ex.GetType().Name);
        }

        await linked.CancelAsync().ConfigureAwait(false);
        StartupWarmupDiagnostics.RecordStep(step.Name, outcome, timeProvider.GetElapsedTime(stepStart).TotalSeconds);
    }

    [LoggerMessage(EventId = 15101, Level = LogLevel.Warning, Message = "Startup warm-up step {Step} failed with {ExceptionType}")]
    private static partial void LogStepFailed(ILogger logger, string step, string exceptionType);

    [LoggerMessage(EventId = 15102, Level = LogLevel.Warning, Message = "Startup warm-up step {Step} exceeded the warm-up budget and no longer blocks readiness")]
    private static partial void LogStepTimedOut(ILogger logger, string step);

    [LoggerMessage(EventId = 15103, Level = LogLevel.Warning, Message = "Startup warm-up step {Step} skipped: warm-up budget exhausted")]
    private static partial void LogStepSkipped(ILogger logger, string step);

    [LoggerMessage(EventId = 15104, Level = LogLevel.Information, Message = "Startup warm-up finished in {ElapsedMs:F0} ms")]
    private static partial void LogFinished(ILogger logger, double elapsedMs);
}
