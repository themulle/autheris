#if DEBUG
namespace TrinoSqlEngine;

using System;
using System.Threading;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Test seam of the governed compile pipeline (plan M-3, CR-ADG-04, CR-ADG-19). It exists only in Debug builds: the type and
/// every call site are compiled out of Release, so production code can neither reference nor enable it.
/// A test installs a seam for the current async flow and the pipeline lets it replace the injector output, corrupt the emitted
/// text before the invariant checker, or observe (and cancel from) the start of every pass. A mutant that skips the coverage
/// verifier, the emitted-text checker, or inserts a cache entry before verification then fails a test.
/// </summary>
internal sealed class CompilerTestSeams
{
    private static readonly AsyncLocal<CompilerTestSeams?> Scope = new();

    /// <summary>The seam of the current async flow, or null.</summary>
    public static CompilerTestSeams? Current => Scope.Value;

    /// <summary>Replaces the output of the typed security injector (a faulty injector).</summary>
    public Func<SqlStatement, SqlStatement>? FaultyInjector { get; init; }

    /// <summary>Replaces the emitted SQL text before the invariant checker runs (a faulty emitter).</summary>
    public Func<string, string>? FaultyEmitter { get; init; }

    /// <summary>Called with the pass name at the start of every pass: parse, build, validate, simplify, inject, verify, capabilities, emit.</summary>
    public Action<string>? OnPass { get; init; }

    /// <summary>Installs the seam for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable Use(CompilerTestSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        var previous = Scope.Value;
        Scope.Value = seams;
        return new Restore(previous);
    }

    private sealed class Restore(CompilerTestSeams? previous) : IDisposable
    {
        public void Dispose() => Scope.Value = previous;
    }
}
#endif
