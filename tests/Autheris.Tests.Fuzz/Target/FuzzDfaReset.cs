using TrinoSqlEngine.Parsing;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>
/// Bounds the process-wide ANTLR caches during a long AFL run (SEC-PF-07). The engine binds every parser and lexer to a cache generation
/// (ARCH-MNT-15); the reset publishes a fresh generation of <see cref="AntlrCacheGenerations.Shared"/>. The old generation is not cleared: it
/// stays valid for in-flight work and is collected afterwards. (Clearing the static DFA arrays of the generated classes would be a silent
/// no-op for production parsers now.)
/// A deterministic execution counter fires the reset every <see cref="DefaultInterval"/> inputs, only at an input boundary on the harness
/// thread and never while an input thread abandoned by a timeout is still alive. It never swallows or retries
/// <see cref="OutOfMemoryException"/>.
/// </summary>
public sealed class FuzzDfaReset
{
    /// <summary>Executions between two resets (the DFA grows ~6 MB per 100k distinct inputs).</summary>
    public const long DefaultInterval = 250_000;

    private readonly long _interval;
    private readonly Action _reset;
    private long _executions;
    private bool _pending;

    /// <param name="interval">Executions between resets (at least 1).</param>
    /// <param name="reset">Test seam; the default swaps the shared ANTLR cache generation.</param>
    public FuzzDfaReset(long interval = DefaultInterval, Action? reset = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
        _interval = interval;
        _reset = reset ?? ClearSharedDfa;
    }

    /// <summary>The process-wide instance used by <see cref="FuzzRunner.RunOne"/>.</summary>
    public static FuzzDfaReset Shared { get; } = new();

    /// <summary>Executions counted so far.</summary>
    public long Executions => _executions;

    /// <summary>Counts one execution at an input boundary (before the input thread starts); runs the reset when due and safe.</summary>
    /// <param name="abandonedInputThreadAlive">True while a thread abandoned by a <c>Join</c> timeout may still use the DFA.</param>
    /// <returns>True when the reset ran.</returns>
    public bool OnInputBoundary(bool abandonedInputThreadAlive)
    {
        _executions++;
        if (_executions % _interval == 0)
        {
            _pending = true;
        }

        if (!_pending || abandonedInputThreadAlive)
        {
            return false;
        }

        _pending = false;
        _reset();
        return true;
    }

    private static void ClearSharedDfa() => AntlrCacheGenerations.Shared.ForceSwap();
}
