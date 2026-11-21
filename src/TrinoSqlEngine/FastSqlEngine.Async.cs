namespace TrinoSqlEngine;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Parsing;

public sealed partial class FastSqlEngine
{
    public Task<(SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens)> ParseAsync(
        ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = PrepareParse(sql, StatementRule, "SQL query", tokenOptions);
        return RunWithParseBudgetAsync(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, StatementRule, static parser => parser.singleStatement()),
            sql.Length,
            cancellationToken);
    }

    public Task<(SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens)> ParseExpressionAsync(
        ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = PrepareParse(sql, ExpressionRule, "SQL expression", tokenOptions);
        return RunWithParseBudgetAsync(
            cancellation => ParseOnCurrentThread(sql, effectiveOptions, cancellation, ExpressionRule, static parser => parser.standaloneExpression()),
            sql.Length,
            cancellationToken);
    }

    public async Task<SqlQueryMetadata> AnalyzeAsync(
        ReadOnlyMemory<char> sql, SqlTokenSecurityOptions? tokenOptions = null, CancellationToken cancellationToken = default)
    {
        var (tree, _) = await ParseAsync(sql, tokenOptions, cancellationToken).ConfigureAwait(false);
        return new SqlQueryAnalyzer().Analyze(tree);
    }

    private async Task<TResult> RunWithParseBudgetAsync<TResult>(Func<ParseCancellation, TResult> work, int sqlLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan budget = ParseTimeout;
        long startTimestamp = Stopwatch.GetTimestamp();
        SemaphoreSlim limiter = ParseConcurrencyLimiter ?? SharedParseConcurrencyLimiter;

        if (!await limiter.WaitAsync(RemainingMilliseconds(budget, startTimestamp), cancellationToken).ConfigureAwait(false))
            throw RejectedParse("all concurrent parser slots are in use and none became free within the parse time budget.");

        var cancellation = new ParseCancellation();
        if (InlineParseMaxLength > 0 && sqlLength <= InlineParseMaxLength && RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            try
            {
                if (budget > TimeSpan.Zero)
                    cancellation.TightenDeadline(budget - Stopwatch.GetElapsedTime(startTimestamp), $"SQL parsing exceeded the time budget of {budget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms.");
                return work(cancellation);
            }
            finally { limiter.Release(); }
        }

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new ParseWork(
            () =>
            {
                TResult? res = default;
                Exception? error = null;
                try { res = work(cancellation); }
                catch (Exception ex) { error = ex; }
                finally
                {
                    try { limiter.Release(); }
                    finally
                    {
                        if (error is not null) tcs.TrySetException(error);
                        else tcs.TrySetResult(res!);
                    }
                }
            },
            onCanceledWhileQueued: () =>
            {
                try { limiter.Release(); }
                finally { tcs.TrySetCanceled(cancellationToken); }
            });

        var pool = ParseWorkerPool.ForStackSize(_parseThreadStackSize);
        bool queued;
        try { queued = pool.TrySubmit(item, RemainingMilliseconds(budget, startTimestamp), cancellationToken); }
        catch { limiter.Release(); throw; }
        if (!queued) { limiter.Release(); throw RejectedParse("the parser queue stayed full within the parse time budget."); }

        try
        {
            if (budget > TimeSpan.Zero)
            {
                var remaining = budget - Stopwatch.GetElapsedTime(startTimestamp);
                return await tcs.Task.WaitAsync(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, cancellationToken).ConfigureAwait(false);
            }
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellation.Cancel();
            pool.Abandon(item);
            ObserveAbandonedFault(tcs.Task);
            throw;
        }
        catch (TimeoutException) when (budget > TimeSpan.Zero)
        {
            cancellation.Cancel();
            pool.Abandon(item);
            ObserveAbandonedFault(tcs.Task);
            throw new ParseCanceledException(
                $"SQL parsing exceeded the time budget of {budget.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms.",
                new TimeoutException("SQL parsing exceeded the configured time budget."));
        }
    }

    private static void ObserveAbandonedFault<T>(Task<T> task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
