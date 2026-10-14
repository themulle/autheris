namespace TrinoSqlEngine;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Stack guard of the compile pipeline (SEC-ADG-05): a <see cref="StackOverflowException"/> cannot be caught, so deep trees are
/// rejected before the stack runs out. Without installed test hooks (always in production) this is exactly <see cref="RuntimeHelpers.EnsureSufficientExecutionStack"/>.
/// Test hooks can simulate stack exhaustion deterministically or switch the guard off (WP-X0v mutant).
/// </summary>
internal static class ExecutionStackGuard
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ensure()
    {
        if (CompilerHooks.Current.BypassesExecutionStackGuard())
        {
            return;
        }

        RuntimeHelpers.EnsureSufficientExecutionStack();
    }
}
