namespace TrinoSqlEngine;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Stack guard of the compile pipeline (SEC-ADG-05): a <see cref="StackOverflowException"/> cannot be caught, so deep trees are
/// rejected before the stack runs out. In Release this is exactly <see cref="RuntimeHelpers.EnsureSufficientExecutionStack"/>.
/// In Debug builds a test seam can simulate stack exhaustion deterministically or switch the guard off (WP-X0v mutant).
/// </summary>
internal static class ExecutionStackGuard
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ensure()
    {
#if DEBUG
        var seam = CompilerTestSeams.Current;
        if (seam is not null)
        {
            if (seam.DisabledControls?.Contains(SecurityControl.ExecutionStackGuard) == true)
            {
                return;
            }

            if (seam.ExhaustStackAtGuardCall is { } at && System.Threading.Interlocked.Increment(ref seam.GuardCalls) == at)
            {
                throw new InsufficientExecutionStackException();
            }
        }
#endif
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }
}
