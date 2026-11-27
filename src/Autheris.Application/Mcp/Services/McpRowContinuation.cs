namespace Autheris.Application.Mcp.Services;

using System;
using System.Threading;

/// <summary>
/// Per-call ambient hand-over of the continuation minter of a <c>query_sql</c> page. The SQL executor that issued the page offers a
/// function that signs a token for "after the first k rows of this page"; only the guardrail opens a scope (<see cref="Begin"/>) and reads
/// the offer when it shortens the result to whole rows. The holder is created per call and set-once, so a later offer never replaces the
/// first one.
/// </summary>
public static class McpRowContinuation
{
    private static readonly AsyncLocal<Holder?> Current = new();

    /// <summary>Opens a scope for one tool call. Synchronous on purpose, so the holder is set in the caller's execution context.</summary>
    public static McpRowContinuationScope Begin()
    {
        var previous = Current.Value;
        var holder = new Holder();
        Current.Value = holder;
        return new McpRowContinuationScope(holder, previous);
    }

    /// <summary>Records the continuation minter of the producer. A no-op without an open scope or without a minter; the first offer wins.</summary>
    public static void Offer(Func<int, string?>? resumeAfter)
    {
        var holder = Current.Value;
        if (holder is null || resumeAfter is null)
        {
            return;
        }

        Interlocked.CompareExchange(ref holder.ResumeAfter, resumeAfter, null);
    }

    internal static void Restore(Holder? previous) => Current.Value = previous;

    internal sealed class Holder
    {
        public Func<int, string?>? ResumeAfter;
    }
}

/// <summary>The scope of one tool call. <see cref="ResumeAfter"/> is null when no producer offered a minter.</summary>
public sealed class McpRowContinuationScope : IDisposable
{
    private readonly McpRowContinuation.Holder _holder;
    private readonly McpRowContinuation.Holder? _previous;
    private int _disposed;

    internal McpRowContinuationScope(McpRowContinuation.Holder holder, McpRowContinuation.Holder? previous)
    {
        _holder = holder;
        _previous = previous;
    }

    public Func<int, string?>? ResumeAfter => Volatile.Read(ref _holder.ResumeAfter);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            McpRowContinuation.Restore(_previous);
        }
    }
}
