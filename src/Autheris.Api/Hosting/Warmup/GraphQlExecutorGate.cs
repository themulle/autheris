namespace Autheris.Api.Hosting.Warmup;

using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// PLAN-OPS-150 A-150-1/-2/-4/-5: drives the executor probe. Ready means <c>GetExecutorAsync</c> returned an executor X and either
/// <see cref="GraphQlWarmupProof.WarmedExecutor"/> is X (warm-up task path) or the gate's own run of the constant <c>{ __typename }</c>
/// (in-process marker, empty context, no principal) proved X. A build exception sets <see cref="ExecutorGateState.Failed"/> and retries
/// with back-off (5 s doubling, cap 60 s); the global warm-up timeout never opens the gate. Once Ready it latches; later
/// <c>ExecutorEvicted</c> events trigger one coalesced eager probe (SEC-OPS150-15) whose failure never flips readiness.
/// </summary>
public sealed partial class GraphQlExecutorGate(
    IRequestExecutorProvider executorProvider,
    IOptions<GatewayOptions> gatewayOptions,
    TimeProvider timeProvider,
    ILogger<GraphQlExecutorGate> logger,
    GraphQlWarmupProof? proof = null,
    IHttpContextAccessor? httpContextAccessor = null,
    IRequestExecutorEvents? executorEvents = null,
    IOptions<StartupWarmupOptions>? warmupOptions = null) : BackgroundService, IGraphQlExecutorGate
{
    internal const string WarmupDocument = GraphQlWarmupRun.Document;
    internal const string StepName = "GraphQlExecutor";
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private int _state;
    private int _hangLogged;
    private int _evictionPending;
    private int _evictionRunning;
    private CancellationToken _stopping;
    private IDisposable? _subscription;

    /// <summary>The currently running (or last) eviction worker; completes when evictions are drained.</summary>
    internal Task EvictionWorker { get; private set; } = Task.CompletedTask;

    public ExecutorGateState State => (ExecutorGateState)Volatile.Read(ref _state);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Synchronous part of the host start: subscribe before the first probe so no eviction is missed.
        _subscription ??= executorEvents?.Subscribe(new EvictionObserver(this));
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EvictionWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping: the worker observes the cancelled stopping token; nothing else to do.
        }
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        await Task.Yield();

        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            ExecutorGateState result;
            try
            {
                result = await RunProbeAsync(stoppingToken).WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            Volatile.Write(ref _state, (int)result);
            if (result == ExecutorGateState.Ready)
            {
                return;
            }

            try
            {
                await Task.Delay(backoff, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = NextBackoff(backoff);
        }
    }

    private static TimeSpan NextBackoff(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

    /// <summary>SEC-OPS150-05: the probe never inherits an ambient request context.</summary>
    private Task<ExecutorGateState> RunProbeAsync(CancellationToken ct)
    {
        Task<ExecutorGateState> probe;
        using (ExecutionContext.SuppressFlow())
        {
            probe = Task.Run(() => ProbeAsync(ct), CancellationToken.None);
        }

        // A probe that never returns is abandoned on host stop only; observe its fault so it is never unobserved.
        _ = probe.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return probe;
    }

    private async Task<ExecutorGateState> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var executor = await GetExecutorWatchedAsync(ct).ConfigureAwait(false);

            // A-150-5 / SEC-OPS150-16: an ambient caller context means the work must not run here; stay Pending, never Ready.
            if (httpContextAccessor?.HttpContext is not null)
            {
                LogAmbientContext(logger);
                StartupWarmupDiagnostics.RecordStep(StepName, StartupWarmupDiagnostics.Failed, 0);
                return ExecutorGateState.Pending;
            }

            if (proof is not null && ReferenceEquals(proof.WarmedExecutor, executor))
            {
                return ExecutorGateState.Ready;
            }

            // Fallback when the warm-up task did not run for this instance. Under PersistedQueriesOnly the constant is rejected by
            // TrustedDocumentsOnlyMiddleware; exactly that rejection proves the pipeline ran. It is never added to the trusted store.
            if (await GraphQlWarmupRun.ProveAsync(executor, gatewayOptions.Value.GraphQL.PersistedQueriesOnly, ct).ConfigureAwait(false))
            {
                proof?.Record(executor);
                return ExecutorGateState.Ready;
            }

            LogNotProven(logger);
            return ExecutorGateState.Pending;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogBuildFailed(logger, ex.GetType().Name);
            return ExecutorGateState.Failed;
        }
    }

    /// <summary>SEC-OPS150-16: a <c>GetExecutorAsync</c> that has not returned after the warm-up timeout is logged once; the gate stays Pending.</summary>
    private async Task<IRequestExecutor> GetExecutorWatchedAsync(CancellationToken ct)
    {
        var pending = executorProvider.GetExecutorAsync(cancellationToken: ct).AsTask();
        if (warmupOptions is null || pending.IsCompleted)
        {
            return await pending.ConfigureAwait(false);
        }

        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WatchHangAsync(TimeSpan.FromSeconds(warmupOptions.Value.TimeoutSeconds), watchCts.Token);
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task WatchHangAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Task.Delay(timeout, timeProvider, ct).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _hangLogged, 1) == 0)
            {
                LogHang(logger, (int)timeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // The call returned (or the host is stopping) before the timeout.
        }
    }

    private void OnEvicted()
    {
        try
        {
            // Before Ready the back-off loop is already probing; a latched gate re-probes eagerly (A-150-4).
            if (State != ExecutorGateState.Ready || _stopping.IsCancellationRequested)
            {
                return;
            }

            Volatile.Write(ref _evictionPending, 1);
            if (Interlocked.CompareExchange(ref _evictionRunning, 1, 0) != 0)
            {
                return; // coalesced into the follow-up of the running worker
            }

            // The event can fire on a request thread during a catalog refresh (SEC-OPS150-05).
            using (ExecutionContext.SuppressFlow())
            {
                EvictionWorker = Task.Run(EvictionWorkerAsync, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Never throw into the HotChocolate observer (SEC-OPS150-15).
            LogEvictionHandlerFailed(logger, ex.GetType().Name);
        }
    }

    private async Task EvictionWorkerAsync()
    {
        try
        {
            while (true)
            {
                while (Interlocked.Exchange(ref _evictionPending, 0) == 1)
                {
                    await ProbeAfterEvictionAsync().ConfigureAwait(false);
                }

                Volatile.Write(ref _evictionRunning, 0);
                if (Volatile.Read(ref _evictionPending) == 0 || Interlocked.CompareExchange(ref _evictionRunning, 1, 0) != 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
        catch (Exception ex)
        {
            LogEvictionHandlerFailed(logger, ex.GetType().Name);
        }
    }

    /// <summary>Retries with the same back-off (cap 60 s) until a build succeeds; the latched gate state is never touched.</summary>
    private async Task ProbeAfterEvictionAsync()
    {
        var backoff = InitialBackoff;
        while (!_stopping.IsCancellationRequested)
        {
            var start = timeProvider.GetTimestamp();
            var result = await ProbeAsync(_stopping).ConfigureAwait(false);
            var seconds = timeProvider.GetElapsedTime(start).TotalSeconds;
            if (result == ExecutorGateState.Ready)
            {
                StartupWarmupDiagnostics.RecordStep(StepName, StartupWarmupDiagnostics.Completed, seconds);
                return;
            }

            StartupWarmupDiagnostics.RecordStep(StepName, StartupWarmupDiagnostics.Failed, seconds);
            await Task.Delay(backoff, timeProvider, _stopping).ConfigureAwait(false);
            backoff = NextBackoff(backoff);
        }
    }

    private sealed class EvictionObserver(GraphQlExecutorGate owner) : IObserver<RequestExecutorEvent>
    {
        public void OnNext(RequestExecutorEvent value)
        {
            if (value.Type == RequestExecutorEventType.Evicted)
            {
                owner.OnEvicted();
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    [LoggerMessage(EventId = 15111, Level = LogLevel.Error, Message = "GraphQL executor build failed with {ExceptionType}; the executor is not ready")]
    private static partial void LogBuildFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 15112, Level = LogLevel.Warning, Message = "GraphQL executor built but the warm-up document did not prove it; the executor is not ready")]
    private static partial void LogNotProven(ILogger logger);

    [LoggerMessage(EventId = 15113, Level = LogLevel.Warning, Message = "GraphQL executor probe skipped: an ambient HttpContext is present; readiness stays 503")]
    private static partial void LogAmbientContext(ILogger logger);

    [LoggerMessage(EventId = 15114, Level = LogLevel.Error, Message = "GetExecutorAsync did not return within {TimeoutSeconds} s; readiness stays 503")]
    private static partial void LogHang(ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 15115, Level = LogLevel.Warning, Message = "GraphQL executor eviction handling failed with {ExceptionType}")]
    private static partial void LogEvictionHandlerFailed(ILogger logger, string exceptionType);
}
