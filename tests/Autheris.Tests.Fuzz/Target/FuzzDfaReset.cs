using TrinoSqlEngine;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>
/// Bounds the process-wide ANTLR caches (static <c>decisionToDFA</c> of the generated parser and lexer) during a long AFL run (SEC-PF-07).
/// A deterministic execution counter fires the reset every <see cref="DefaultInterval"/> inputs, only at an input boundary on the harness
/// thread and never while an input thread abandoned by a timeout is still alive. It never swallows or retries
/// <see cref="OutOfMemoryException"/>; harness only, the engine under src/ is unchanged.
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
    /// <param name="reset">Test seam; the default clears the shared parser and lexer DFA.</param>
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

    private static void ClearSharedDfa()
    {
        // The DFA arrays are static, so one instance of each generated class reaches every pooled parser and lexer.
        new SqlBaseParser(null).Interpreter.ClearDFA();
        new SqlBaseLexer(null).Interpreter.ClearDFA();
    }
}
