namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;

/// <summary>
/// Answers whether a column of a table is masked. The predicate guards of the security visitor ask it; it never renders SQL, and the
/// masks themselves are typed nodes (<see cref="Nodes.MaskExpression"/>) built from <see cref="Governance.IColumnMaskProvider"/>.
/// </summary>
internal interface IMaskedColumnLookup
{
    bool HasMask(string normalizedTableName, string columnName);
}

/// <summary>
/// The switches of <see cref="AstSecurityVisitor"/> for one compile. They are derived by <see cref="GovernedSqlCompiler"/> from the
/// immutable <see cref="CompileRequest"/>; there is no caller-built options bag (it replaced the legacy mutable <c>RlsOptions</c>).
/// </summary>
internal sealed record AstSecurityVisitorOptions
{
    public required IMaskedColumnLookup MaskedColumns { get; init; }

    /// <summary>Names of the cataloged columns of a table (normalized name), or null when the table is not in the catalog.</summary>
    public required Func<string, IReadOnlyList<string>?> TableColumns { get; init; }

    public IReadOnlySet<string> TablesWithMaskedColumns { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clamps or injects a row cap on the root read (0 = disabled).</summary>
    public long EnforcedMaxRows { get; init; }

    public IReadOnlySet<string> TablesWithConsentRowFilter { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool RejectMaskedColumnsInPredicates { get; init; } = true;
    public bool RejectMaskedColumnsInDml { get; init; } = true;
    public bool RejectUnfilteredDml { get; init; } = true;
    public bool RequireTenantColumnInInsert { get; init; } = true;
    public bool DisallowTenantColumnModificationInUpdate { get; init; } = true;
    public bool RejectPolicyColumnAssignment { get; init; } = true;
    public bool RejectConsentFilteredInsert { get; init; } = true;
    public bool RejectWholeRowReferencesInDml { get; init; } = true;

    /// <summary>
    /// A column with a mask must not be an argument of an aggregate function (including its FILTER and ordered-aggregate ORDER BY) or a
    /// grouping expression. The aggregate would run over mask output, which is meaningless at best and an inference channel at worst
    /// (SR-F AGGREGATION_ON_MASKED_COLUMN). Fail closed: default true, not derived from the caller.
    /// </summary>
    public bool RejectMaskedColumnsInAggregates { get; init; } = true;

    /// <summary>
    /// A whole-row reference (<c>SELECT t FROM t</c>, <c>to_json(t)</c>) to a table with masked columns is rejected in SELECT
    /// projections (SR-L-24). Mirrors <see cref="RejectWholeRowReferencesInDml"/>.
    /// </summary>
    public bool RejectWholeRowReferencesInSelect { get; init; } = true;
    public bool EnforceWithCheckOption { get; init; } = true;

    /// <summary>Issue #277 (SEC-277-03): a <c>WITH RECURSIVE</c> compiles only when true. Default false; DML rejects it regardless.</summary>
    public bool AllowRecursiveCte { get; init; }
}
