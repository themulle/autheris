namespace TrinoSqlEngine;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine.Governance;

/// <summary>Statement classes a request allows. DML is not yet supported by the compiler and is rejected (fail closed).</summary>
[Flags]
public enum StatementPermissions { ReadOnly = 0, Insert = 1, Update = 2, Delete = 4, Merge = 8 }

public enum GovernedSubqueryStrategy { Correlated = 0 }

/// <summary>DML guard switches (plan 4.1). Carried for the DML work package; the SELECT compiler rejects DML.</summary>
public sealed record DmlGuardOptions(
    bool EnforceWithCheckOption,
    bool RequireTenantColumnInInsert,
    bool DisallowTenantColumnModificationInUpdate,
    bool RejectUnfilteredDml,
    bool RejectMaskedColumnsInDml,
    bool RejectMaskedColumnsInPredicates,
    bool RejectConsentFilteredInsert,
    bool RejectWholeRowReferencesInDml)
{
    public static DmlGuardOptions Strict { get; } = new(true, true, true, true, true, true, true, true);
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

    /// <summary>The compiler always projects only cataloged columns; the flag exists so the key reflects the caller's intent.</summary>
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
