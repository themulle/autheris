namespace Autheris.Api.Hosting.Warmup;

using System.Text.Json;
using Autheris.Domain.Options;
using HotChocolate.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// PLAN-OPS-150 A-150-1/-2 (WP-B scope): drives the executor probe. Positive proof means <c>GetExecutorAsync</c> returned an
/// executor and the compiled-in constant <c>{ __typename }</c> (in-process warm-up marker, empty context, no principal) returned
/// <c>data.__typename == "Query"</c> without errors. A build exception sets <see cref="ExecutorGateState.Failed"/> and retries with
/// back-off (5 s doubling, cap 60 s); the global warm-up timeout never opens the gate. Once Ready it latches.
/// Left for WP-C: warm-up task and <c>WarmedExecutor</c> identity, eviction subscription with probe coalescing
/// (SEC-OPS150-15), ambient <c>HttpContext</c> assertion and the log-once-after-timeout path (SEC-OPS150-16).
/// </summary>
public sealed partial class GraphQlExecutorGate(
    IRequestExecutorProvider executorProvider,
    IOptions<GatewayOptions> gatewayOptions,
    TimeProvider timeProvider,
    ILogger<GraphQlExecutorGate> logger) : BackgroundService, IGraphQlExecutorGate
{
    internal const string WarmupDocument = "{ __typename }";
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private int _state;

    public ExecutorGateState State => (ExecutorGateState)Volatile.Read(ref _state);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            // SEC-OPS150-05: the probe never inherits an ambient request context.
            Task<ExecutorGateState> probe;
            using (ExecutionContext.SuppressFlow())
            {
                probe = Task.Run(() => ProbeAsync(stoppingToken), CancellationToken.None);
            }

            ExecutorGateState result;
            try
            {
                result = await probe.ConfigureAwait(false);
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

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task<ExecutorGateState> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var executor = await executorProvider.GetExecutorAsync(cancellationToken: ct).ConfigureAwait(false);

            // PersistedQueriesOnly rejects every non-allow-listed document, including the constant: a successful build is the proof.
            if (gatewayOptions.Value.GraphQL.PersistedQueriesOnly)
            {
                return ExecutorGateState.Ready;
            }

            var request = OperationRequestBuilder.New().SetDocument(WarmupDocument).MarkAsWarmupRequest().Build();
            var executed = await executor.ExecuteAsync(request, ct).ConfigureAwait(false);
            if (IsTypenameQuery(executed))
            {
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

    private static bool IsTypenameQuery(IExecutionResult result)
    {
        if (result is not OperationResult op || op.Errors is { Count: > 0 })
        {
            return false;
        }

        using var doc = JsonDocument.Parse(op.ToJson());
        return doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("__typename", out var name)
            && name.ValueKind == JsonValueKind.String
            && name.GetString() == "Query";
    }

    [LoggerMessage(EventId = 15111, Level = LogLevel.Error, Message = "GraphQL executor build failed with {ExceptionType}; readiness stays 503 until a build succeeds")]
    private static partial void LogBuildFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 15112, Level = LogLevel.Warning, Message = "GraphQL executor built but the warm-up document did not prove it; readiness stays 503")]
    private static partial void LogNotProven(ILogger logger);
}
