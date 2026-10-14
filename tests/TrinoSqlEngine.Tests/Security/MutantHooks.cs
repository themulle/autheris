using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// Test-only implementation of <see cref="ICompilerHooks"/> (plan M-3, CR-ADG-04, CR-ADG-19, ARC-S-06). It lives in the test assembly, so
/// the shipped assemblies contain no switch that can turn a control off. A test installs it for the current async flow with
/// <see cref="CompilerHooks.Use"/>; the pipeline then replaces the injector output, corrupts the emitted text before the invariant
/// checker, observes the start of every pass, or switches a named <see cref="SecurityControl"/> off (a mutant).
/// </summary>
internal sealed class MutantHooks : ICompilerHooks
{
    /// <summary>Replaces the output of the typed security injector (a faulty injector).</summary>
    public Func<SqlStatement, SqlStatement>? FaultyInjector { get; init; }

    /// <summary>Replaces the emitted SQL text before the invariant checker runs (a faulty emitter).</summary>
    public Func<string, string>? FaultyEmitter { get; init; }

    /// <summary>Called with the pass name at the start of every pass.</summary>
    public Action<string>? OnPass { get; init; }

    /// <summary>Security controls that are switched off for the current async flow (a mutant). Empty by default.</summary>
    public IReadOnlySet<SecurityControl>? DisabledControls { get; init; }

    /// <summary>Top-level <c>CompileRequest</c> property names that the plan-cache key omits (a cache-poisoning mutant).</summary>
    public IReadOnlySet<string>? OmittedCacheKeyFields { get; init; }

    /// <summary>Simulated stack exhaustion: the guard throws <see cref="InsufficientExecutionStackException"/> at the given call number (1-based).</summary>
    public int? ExhaustStackAtGuardCall { get; init; }

    /// <summary>Replaces the (validated, strict) DML guard options for the rest of one compile (a mutant that relaxes a guard).</summary>
    public Func<DmlGuardOptions, DmlGuardOptions>? DmlGuardMutator { get; init; }

    internal int GuardCalls;

    void ICompilerHooks.OnPass(string name) => OnPass?.Invoke(name);

    public bool IsDisabled(SecurityControl control) => DisabledControls?.Contains(control) == true;

    public SqlStatement InterceptInjected(SqlStatement secured) => FaultyInjector is { } injector ? injector(secured) : secured;

    public string InterceptEmitted(string sql) => FaultyEmitter?.Invoke(sql) ?? sql;

    public bool OmitsCacheKeyField(string name) => OmittedCacheKeyFields?.Contains(name) == true;

    public DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested) => DmlGuardMutator is { } mutate ? mutate(requested) : requested;

    public bool BypassesExecutionStackGuard()
    {
        if (IsDisabled(SecurityControl.ExecutionStackGuard))
        {
            return true;
        }

        if (ExhaustStackAtGuardCall is { } at && Interlocked.Increment(ref GuardCalls) == at)
        {
            throw new InsufficientExecutionStackException();
        }

        return false;
    }

    public GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect) =>
        IsDisabled(SecurityControl.ErrorSanitizer) ? new LeakingGovernedSqlException(providerMessage, dialect) : null;
}

/// <summary>Mutant of <c>DmlErrorSanitizer</c> (test code only): an error that carries the provider message.</summary>
internal sealed class LeakingGovernedSqlException(string message, TargetSqlDialect dialect)
    : GovernedSqlException(GovernedSqlErrorCodes.ProviderError, dialect)
{
    public override string Message { get; } = message;
}
