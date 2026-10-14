using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Microsoft.Extensions.ObjectPool;

namespace TrinoSqlEngine;

/// <summary>
/// SQ-08: Cooperative cancellation flag for a single parse. ANTLR has no native cancellation; the flag is checked by
/// <see cref="CancellableTokenStream"/> on every token access (adaptive prediction and matching).
/// </summary>
internal sealed class ParseCancellation
{
    private volatile bool _canceled;

    // Deadline of the thread that runs the parse (read and written on that thread only); 0 = none.
    private long _deadlineTimestamp;
    private string _deadlineMessage = string.Empty;
    private int _ticks;

    public bool IsCanceled => _canceled;

    public void Cancel() => _canceled = true;

    /// <summary>
    /// ARC-S-09: lets the running parse abort itself after <paramref name="budget"/> (the LL fallback budget and the inline path).
    /// A deadline can only be tightened. It surfaces as a <see cref="ParseCanceledException"/> with a <see cref="TimeoutException"/> cause.
    /// </summary>
    public void TightenDeadline(TimeSpan budget, string message)
    {
        long candidate = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        if (_deadlineTimestamp == 0 || candidate < _deadlineTimestamp)
        {
            _deadlineTimestamp = candidate;
            _deadlineMessage = message;
        }
    }

    public void ThrowIfCanceled()
    {
        if (_canceled)
        {
            throw new ParseCanceledException("SQL parsing was aborted because the parse time budget was exceeded or the caller canceled the request.");
        }

        if (_deadlineTimestamp != 0 && (++_ticks & 63) == 0 && Stopwatch.GetTimestamp() > _deadlineTimestamp)
        {
            throw new ParseCanceledException(_deadlineMessage, new TimeoutException(_deadlineMessage));
        }
    }
}

/// <summary>
/// SQ-08: Token stream that aborts the parser (via <see cref="ParseCanceledException"/>) as soon as the parse is canceled.
/// </summary>
internal sealed class CancellableTokenStream : CommonTokenStream
{
    private readonly ParseCancellation _cancellation;

    public CancellableTokenStream(ITokenSource tokenSource, ParseCancellation cancellation)
        : base(tokenSource)
    {
        _cancellation = cancellation;
    }

    public override IToken LT(int k)
    {
        _cancellation.ThrowIfCanceled();
        return base.LT(k);
    }

    public override int LA(int i)
    {
        _cancellation.ThrowIfCanceled();
        return base.LA(i);
    }

    public override void Consume()
    {
        _cancellation.ThrowIfCanceled();
        base.Consume();
    }
}
