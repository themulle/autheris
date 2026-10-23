namespace TrinoSqlEngine;

using System;
using System.Threading;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Hooks of the governed compile pipeline (plan M-3, CR-ADG-04, CR-ADG-19; ARC-S-06). Debug and Release compile the same security
/// code: every call site asks the hooks, and the production answer (<see cref="NoOpCompilerHooks"/>) is always "all controls
/// enabled, nothing replaced". Only a test assembly (InternalsVisibleTo) can install another implementation, and no implementation
/// other than <see cref="NoOpCompilerHooks"/> exists in the shipped assembly (<c>ReleaseHookAbsenceTests</c>). There is no
/// configuration, environment variable or DI registration that installs a hook.
/// </summary>
internal interface ICompilerHooks
{
    /// <summary>True when <paramref name="control"/> is switched off (a mutant).</summary>
    bool IsDisabled(SecurityControl control);

    /// <summary>Called with the pass name at the start of every pass: parse, build, validate, simplify, inject, verify, capabilities, emit.</summary>
    void OnPass(string name);

    /// <summary>Replaces the output of the typed security injector (a faulty injector).</summary>
    SqlStatement InterceptInjected(SqlStatement secured);

    /// <summary>Replaces the emitted SQL text before the invariant checker runs (a faulty emitter).</summary>
    string InterceptEmitted(string sql);

    /// <summary>True when the named top-level <c>CompileRequest</c> property (or <c>Versions</c>) is omitted from the plan-cache key (a cache-poisoning mutant).</summary>
    bool OmitsCacheKeyField(string name);

    /// <summary>Replaces the (validated, strict) DML guard options for the rest of one compile (a mutant that relaxes a guard).</summary>
    DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested);

    /// <summary>
    /// Stack guard hook. Returns true when the guard is switched off; throws <see cref="InsufficientExecutionStackException"/> for a
    /// simulated stack exhaustion.
    /// </summary>
    bool BypassesExecutionStackGuard();

    /// <summary>The error the provider-error sanitizer returns instead of the fixed typed error (a leaking mutant), or null.</summary>
    GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect);
}

/// <summary>The production hooks: every control is enabled and nothing is replaced.</summary>
internal sealed class NoOpCompilerHooks : ICompilerHooks
{
    public static NoOpCompilerHooks Instance { get; } = new();

    private NoOpCompilerHooks()
    {
    }

    public bool IsDisabled(SecurityControl control) => false;

    public void OnPass(string name)
    {
    }

    public SqlStatement InterceptInjected(SqlStatement secured) => secured;

    public string InterceptEmitted(string sql) => sql;

    public bool OmitsCacheKeyField(string name) => false;

    public DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested) => requested;

    public bool BypassesExecutionStackGuard() => false;

    public GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect) => null;
}

/// <summary>
/// The hooks of the current async flow. Fail-closed: without an installed hook (always the case in production) this is
/// <see cref="NoOpCompilerHooks"/>. <see cref="Use"/> is internal and called by test code only.
/// </summary>
internal static class CompilerHooks
{
    private static readonly AsyncLocal<ICompilerHooks?> Scope = new();
    private static int _installed;

    /// <summary>The hooks of the current async flow; never null.</summary>
    public static ICompilerHooks Current => Volatile.Read(ref _installed) == 0 ? NoOpCompilerHooks.Instance : Scope.Value ?? NoOpCompilerHooks.Instance;

    /// <summary>True when <paramref name="control"/> is switched off for the current async flow (never in production).</summary>
    public static bool IsDisabled(SecurityControl control) => Current.IsDisabled(control);

    /// <summary>Installs the hooks for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable Use(ICompilerHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        var previous = Scope.Value;
        Scope.Value = hooks;
        Interlocked.Increment(ref _installed);
        return new Restore(previous);
    }

    private sealed class Restore(ICompilerHooks? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Scope.Value = previous;
            Interlocked.Decrement(ref _installed);
        }
    }
}

/// <summary>The named security controls a test mutant can switch off through <see cref="ICompilerHooks"/>; production never does (plan M-3 and the WP-X0v Phase 3 amendment).</summary>
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
    MergeClauseClosedSet,

    /// <summary>The checked executor does not enforce <c>CheckedDmlLimits.MaxAffectedRows</c> (X0k).</summary>
    CheckedDmlMaxAffectedRows,

    /// <summary><c>CompiledSqlBinders.For</c> hands out a binder that does not refuse a statement that needs the checked executor (X0k).</summary>
    BinderFactoryRefusal,

    /// <summary>The Databricks tenant comparison is relaxed to a plain equality for a catalog collation of UTF8_BINARY (removed by X0j).</summary>
    DatabricksUtf8BinaryRelaxation,

    /// <summary><c>SessionPinning.EnsurePinned</c> accepts an unverified or failed session pin (X0b).</summary>
    SessionPinReadBack,

    /// <summary>The error mapper returns <c>Exception.Message</c> as the title of the mapped error (X0a).</summary>
    ErrorMapperFixedMessage,

    /// <summary>The tenant registry gate returns <c>Registered</c> when the store is unavailable (X0p).</summary>
    TenantRegistryFailClosed,

    /// <summary>A catalog load failure yields an empty catalog entry instead of a denied table (X0c).</summary>
    CatalogLoadFailureDenies,

    /// <summary>The residual position check of the client list pass is skipped: a list in a scalar position is not refused (SEC-128-10).</summary>
    ClientListPosition,

    /// <summary>The binder does not compare the compiled element count with the supplied list (SEC-128-10).</summary>
    ClientListCount,

    /// <summary>The client list pass runs after the typed security visitor instead of before it (SEC-128-01).</summary>
    ClientListExpandsBeforeGuards,

    /// <summary>The per-statement cap of expanded list items is not enforced (SEC-128-02).</summary>
    ClientListMaxExpanded,

    /// <summary>A cache hit binds the raw value of an escaped LIKE pattern slot (SQLE-10).</summary>
    CacheRebindLikeEscape
}
