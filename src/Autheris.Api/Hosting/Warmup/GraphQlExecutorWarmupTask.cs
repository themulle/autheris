namespace Autheris.Api.Hosting.Warmup;

using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// PLAN-OPS-150 D-150-5 / A-150-2: HotChocolate-native warm-up of every executor build. Runs the constant <c>{ __typename }</c>
/// in-process with <c>MarkAsWarmupRequest</c> (empty context data, no principal) and records the executor as warmed only when the
/// result proves the pipeline. Depends on nothing but the proof, the options, a logger and the accessor, so it cannot reach audit,
/// consent, data sources or rate limiting (SEC-OPS150-17). Never throws into the executor build.
/// </summary>
public sealed partial class GraphQlExecutorWarmupTask(
    GraphQlWarmupProof proof,
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<GraphQlExecutorWarmupTask> logger,
    IHttpContextAccessor? httpContextAccessor = null) : IRequestExecutorWarmupTask
{
    /// <summary>Also runs for rebuilds after a catalog refresh, so the new executor is warm before it serves.</summary>
    public bool ApplyOnlyOnStartup => false;

    public async Task WarmupAsync(IRequestExecutor executor, CancellationToken cancellationToken)
    {
        // A-150-5 defence in depth: a caller context here means the build was triggered on a request thread.
        if (httpContextAccessor?.HttpContext is not null)
        {
            LogAmbientContext(logger);
            StartupWarmupDiagnostics.RecordStep(GraphQlExecutorGate.StepName, StartupWarmupDiagnostics.Failed, 0);
            return;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (await GraphQlWarmupRun.ProveAsync(executor, gatewayOptions.Value.GraphQL.PersistedQueriesOnly, cancellationToken).ConfigureAwait(false))
            {
                proof.Record(executor);
                StartupWarmupDiagnostics.RecordStep(GraphQlExecutorGate.StepName, StartupWarmupDiagnostics.Completed, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
                return;
            }

            LogNotProven(logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex.GetType().Name);
        }

        StartupWarmupDiagnostics.RecordStep(GraphQlExecutorGate.StepName, StartupWarmupDiagnostics.Failed, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    [LoggerMessage(EventId = 15121, Level = LogLevel.Warning, Message = "GraphQL warm-up task skipped: an ambient HttpContext is present")]
    private static partial void LogAmbientContext(ILogger logger);

    [LoggerMessage(EventId = 15122, Level = LogLevel.Warning, Message = "GraphQL warm-up document did not prove the new executor")]
    private static partial void LogNotProven(ILogger logger);

    [LoggerMessage(EventId = 15123, Level = LogLevel.Warning, Message = "GraphQL warm-up task failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
