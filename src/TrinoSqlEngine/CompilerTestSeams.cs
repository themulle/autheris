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
/// WP-X0v (SEC-ADG-13, M-3): a seam can also switch a named security control off (<see cref="DisabledControls"/>); that is how
/// the gate's mutant suite proves that every control is guarded by at least one test. The mutants run on every PR.
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

    /// <summary>Security controls that are switched off for the current async flow (a mutant). Empty by default.</summary>
    public IReadOnlySet<SecurityControl>? DisabledControls { get; init; }

    /// <summary>Top-level <c>CompileRequest</c> property names that the plan-cache key omits (a cache-poisoning mutant).</summary>
    public IReadOnlySet<string>? OmittedCacheKeyFields { get; init; }

    /// <summary>
    /// Simulated stack exhaustion: when set, <see cref="ExecutionStackGuard.Ensure"/> throws <see cref="InsufficientExecutionStackException"/>
    /// at the given call number (1-based) of the flow. Deterministic, never overflows the real stack.
    /// </summary>
    public int? ExhaustStackAtGuardCall { get; init; }

    /// <summary>
    /// Replaces the (validated, strict) DML guard options for the rest of one compile (a mutant that relaxes a guard). The operator is
    /// test code; production code never constructs relaxed options (architecture test CR-ADG-38).
    /// </summary>
    public Func<DmlGuardOptions, DmlGuardOptions>? DmlGuardMutator { get; init; }

    internal int GuardCalls;

    /// <summary>True when <paramref name="control"/> is switched off for the current async flow.</summary>
    public static bool IsDisabled(SecurityControl control) => Scope.Value?.DisabledControls?.Contains(control) == true;

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

/// <summary>The named security controls a mutant can switch off (plan M-3 and the WP-X0v Phase 3 amendment).</summary>
internal enum SecurityControl
{
    /// <summary>The production coverage verifier is not called.</summary>
    CoverageVerifier,

    /// <summary>The emitted-text invariant checker is not called.</summary>
    EmittedTextChecker,

    /// <summary>The tenant comparison is a plain equality (no binary-exact conjunct).</summary>
    TenantByteExactCompare,

    /// <summary>The SQL Server tenant template loses its DATALENGTH conjunct (trailing NUL padding).</summary>
    TenantBinaryLengthConjunct,

    /// <summary>Plain <c>Bind</c> no longer refuses a statement that needs the checked executor.</summary>
    BinderRefusal,

    /// <summary>Plain <c>Bind</c> no longer recognises the reserved <c>autheris_ins</c> alias when the flag was lost.</summary>
    BinderAliasMarker,

    /// <summary>The Oracle binder does not enable and verify <c>BindByName</c>.</summary>
    OracleBindByName,

    /// <summary>The checked executor does not compare the affected row count (<c>DmlCheckOption.Enforce</c> is skipped).</summary>
    CheckedDmlRowCount,

    /// <summary>The checked executor commits before it compares the affected row count.</summary>
    CheckedDmlCommitBeforeEnforce,

    /// <summary>A cache hit does not rebind the policy values of the current request.</summary>
    CacheRebindPolicyValues,

    /// <summary>The error sanitizer lets the provider message through.</summary>
    ErrorSanitizer,

    /// <summary>A <c>${</c> variable substitution sequence is accepted (token guard and Databricks identifier guard).</summary>
    VariableSubstitution,

    /// <summary>The dialect IN-list limit is not enforced.</summary>
    InListLimit,

    /// <summary>The execution-stack guard is a no-op.</summary>
    ExecutionStackGuard,

    /// <summary>A string literal is emitted inline instead of bound.</summary>
    UnboundStringLiteral,

    /// <summary>The CTE scope is case-insensitive (a quoted physical name resolves to a CTE).</summary>
    CteCaseInsensitiveScope,

    /// <summary>The MERGE clause set is no longer closed in the coverage verifier.</summary>
    MergeClauseClosedSet
}
#endif
