namespace Autheris.Api.Hosting.Warmup;

using System.Diagnostics;
using HotChocolate.Execution;
using HotChocolate.Execution.Instrumentation;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLAN-OPS-150 WP-A: logs the duration of the parse, validate, compile and execute stages for the first
/// <see cref="MaxOperations"/> occurrences of each stage. The request context is never read, so no document text, variables, user or
/// tenant can reach the log (SEC-OPS150-12).
/// </summary>
public sealed partial class ColdPathTimingListener(ILogger<ColdPathTimingListener> logger) : ExecutionDiagnosticEventListener
{
    /// <summary>Number of operations that are measured; later operations are not logged.</summary>
    public const int MaxOperations = 3;

    private int _parse;
    private int _validate;
    private int _compile;
    private int _execute;

    public override IDisposable ParseDocument(RequestContext context) => IsWarmup(context) ? EmptyScope : Stage("parse", ref _parse);

    public override IDisposable ValidateDocument(RequestContext context) => IsWarmup(context) ? EmptyScope : Stage("validate", ref _validate);

    public override IDisposable CompileOperation(RequestContext context) => IsWarmup(context) ? EmptyScope : Stage("compile", ref _compile);

    public override IDisposable ExecuteOperation(RequestContext context) => IsWarmup(context) ? EmptyScope : Stage("execute", ref _execute);

    // SEC-OPS150-18: the in-process warm-up request is neither logged nor counted against the budget (the null guard serves unit tests).
    private static bool IsWarmup(RequestContext context) => context is not null && context.IsWarmupRequest();

    // Each stage is counted separately: HotChocolate raises the parse event before the request scope opens, so a shared
    // per-operation counter would misattribute it. The first MaxOperations occurrences of every stage are logged.
    private IDisposable Stage(string stage, ref int counter)
    {
        // Exact bound under concurrency: the increment itself decides, so at most MaxOperations scopes are ever created.
        var occurrence = Interlocked.Increment(ref counter);
        return occurrence > MaxOperations ? EmptyScope : new StageScope(logger, stage, occurrence);
    }

    [LoggerMessage(EventId = 15001, Level = LogLevel.Information, Message = "ColdPath stage {Stage} occurrence {Occurrence} took {ElapsedMs:F1} ms")]
    private static partial void LogStage(ILogger logger, string stage, int occurrence, double elapsedMs);

    private sealed class StageScope(ILogger logger, string stage, int occurrence) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose() =>
            LogStage(logger, stage, occurrence, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }
}
