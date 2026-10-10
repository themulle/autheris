namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Statement classes a request allows in addition to SELECT. A class is compiled only when the request allows it <b>and</b> the
/// dialect capability entry lists it (<see cref="Ast.Capabilities.DialectCapabilities.DmlStatements"/>); otherwise it is rejected (fail closed).
/// </summary>
[Flags]
public enum StatementPermissions { ReadOnly = 0, Insert = 1, Update = 2, Delete = 4, Merge = 8 }

public enum GovernedSubqueryStrategy { Correlated = 0 }

/// <summary>
/// DML guard switches (plan 4.1), enforced by the typed security visitor and proved by the coverage verifier. <see cref="Strict"/> is
/// the only value the governed compiler accepts (CR-ADG-38): any other combination is rejected with a
/// <see cref="SqlCompileConfigurationException"/>. The type stays so that the injector and verifier can be unit-tested against
/// each switch.
/// </summary>
public sealed record DmlGuardOptions(
    bool EnforceWithCheckOption,
    bool RequireTenantColumnInInsert,
    bool DisallowTenantColumnModificationInUpdate,
    bool RejectUnfilteredDml,
    bool RejectMaskedColumnsInDml,
    bool RejectMaskedColumnsInPredicates,
    bool RejectConsentFilteredInsert,
    bool RejectWholeRowReferencesInDml,
    bool RejectPolicyColumnAssignment = true)
{
    public static DmlGuardOptions Strict { get; } = new(true, true, true, true, true, true, true, true, true);
}

/// <summary>Typed governance input of a compile (replaces string providers and the mutable options bag).</summary>
public sealed record GovernancePolicy
{
    public required IPolicyPredicateProvider RowFilters { get; init; }
    public required IColumnMaskProvider Masks { get; init; }
    public required ITableCatalog Catalog { get; init; }
    public required TenantBinding Tenant { get; init; }
    public DmlGuardOptions Dml { get; init; } = DmlGuardOptions.Strict;
    public IReadOnlySet<string> TablesWithConsentRowFilter { get; init; } = FrozenSet<string>.Empty;
    public IReadOnlySet<string> TablesWithMaskedColumns { get; init; } = FrozenSet<string>.Empty;
    public GovernedSubqueryStrategy SubqueryStrategy { get; init; }
}

/// <summary>
/// Immutable compile input. There is no default dialect: a missing or unsupported dialect fails closed. Every property is part
/// of the plan-cache key (see <see cref="Ast.Emit.CompileCacheKey"/>).
/// </summary>
public sealed record CompileRequest
{
    public required TargetSqlDialect TargetDialect { get; init; }
    public required GovernancePolicy Policy { get; init; }
    public required SqlTokenSecurityOptions TokenGuards { get; init; }
    public StatementPermissions Statements { get; init; } = StatementPermissions.ReadOnly;
    public long EnforcedMaxRows { get; init; }
    public bool TranslateTrinoDateFunctions { get; init; }

    /// <summary>
    /// Intentionally has no effect on the output (CR-ADG-23): the compiler always projects only cataloged columns, because the
    /// verifier proves that shape (CR-ADG-06). The flag stays so that the plan-cache key reflects the caller's intent and a future
    /// opt-out would not silently share cached templates with the strict default.
    /// </summary>
    public bool EnforceCatalogProjection { get; init; } = true;

    public IReadOnlySet<string>? AllowedFunctions { get; init; }
    public IReadOnlySet<string>? AllowedTableFunctions { get; init; }

    /// <summary>Test-only (an architecture test forbids setting it in <c>src</c>). Dialects without a production tier stay rejected.</summary>
    public bool AllowExperimentalDialect { get; init; }

    /// <summary>Budget of one compile (SEC-ADG-05). Exceeding it is a typed <see cref="Ast.Capabilities.SqlLimitExceededException"/>.</summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum emitted length divided by the input length (floor 64 characters of input); SEC-ADG-05.</summary>
    public int MaxExpansionFactor { get; init; } = 64;
}

/// <summary>
/// CR-ADG-14 / CR-ADG-15 / SEC-ADG-05: bounds the caller-controlled compile knobs and forces the dialect-mandatory token guards.
/// A non-positive or infinite timeout and a non-positive expansion factor are rejected; larger values are clamped to the maximum.
/// </summary>
public static class CompileLimits
{
    /// <summary>Upper bound of <see cref="CompileRequest.CompileTimeout"/>.</summary>
    public static readonly TimeSpan MaxCompileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of <see cref="CompileRequest.MaxExpansionFactor"/>.</summary>
    public const int MaxExpansionFactorLimit = 256;

    /// <summary>The request the compiler actually runs: validated, clamped, with the mandatory guards of the dialect.</summary>
    public static CompileRequest Normalize(CompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CompileTimeout <= TimeSpan.Zero || request.CompileTimeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "CompileTimeout must be positive and finite; the compile budget cannot be disabled.");
        }

        if (request.MaxExpansionFactor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxExpansionFactor must be at least 1.");
        }

        var timeout = request.CompileTimeout > MaxCompileTimeout ? MaxCompileTimeout : request.CompileTimeout;
        int factor = Math.Min(request.MaxExpansionFactor, MaxExpansionFactorLimit);

        // Databricks resolves ${...} variable substitution in the engine: the guard is not optional (SEC-ADG-10, CR-ADG-15).
        var guards = request.TokenGuards;
        if (request.TargetDialect == TargetSqlDialect.Databricks && !guards.RejectVariableSubstitutionSequences)
        {
            guards = guards with { RejectVariableSubstitutionSequences = true };
        }

        return timeout == request.CompileTimeout && factor == request.MaxExpansionFactor && ReferenceEquals(guards, request.TokenGuards)
            ? request
            : request with { CompileTimeout = timeout, MaxExpansionFactor = factor, TokenGuards = guards };
    }
}

public enum SqlCompileNotSupportedReason { Dialect, StatementClass, Construct }

/// <summary>
/// The statement, dialect or construct is not (yet) supported by the governed compiler. Fail closed: nothing is emitted. The
/// message names the category only, never request text.
/// </summary>
public sealed class SqlCompileNotSupportedException : SecurityException
{
    public SqlCompileNotSupportedReason Reason { get; }

    public SqlCompileNotSupportedException(SqlCompileNotSupportedReason reason, string detail)
        : base($"Not yet supported by the governed SQL compiler: {reason} ({detail}).")
    {
        Reason = reason;
    }
}

/// <summary>
/// CR-ADG-38: the compile request asks for a weaker security posture than the governed path allows (for example a relaxed DML
/// guard). The guards are fixed; they cannot be relaxed per request. The message names no setting and no value.
/// </summary>
public sealed class SqlCompileConfigurationException : SecurityException
{
    public SqlCompileConfigurationException()
        : base("The governed SQL compiler does not allow a security guard to be relaxed per request.")
    {
    }
}
