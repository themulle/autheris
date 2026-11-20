namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Frozen;
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
    /// <summary>Lookup for determining whether a column carries a masking rule.</summary>
    public required IMaskedColumnLookup MaskedColumns { get; init; }

    /// <summary>Names of the cataloged columns of a table (normalized name), or null when the table is not in the catalog.</summary>
    public required Func<string, IReadOnlyList<string>?> TableColumns { get; init; }

    /// <summary>
    /// Catalog data type of a column of a table (normalized table name, column name), or null when unknown. Null (no lookup) switches the
    /// aggregate type guard off; the compiler always sets it (#310).
    /// </summary>
    public Func<string, string, string?>? ColumnDataType { get; init; }

    /// <summary>Normalized names of tables that contain at least one masked column.</summary>
    public IReadOnlySet<string> TablesWithMaskedColumns { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clamps or injects a row cap on the root read (0 = disabled).</summary>
    public long EnforcedMaxRows { get; init; }

    /// <summary>Normalized names of tables subject to an active consent row filter.</summary>
    public IReadOnlySet<string> TablesWithConsentRowFilter { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional DML write permit authorizing INSERT/UPDATE/DELETE compilation.</summary>
    public DmlWritePermit? WritePermit { get; init; }

    /// <summary>Gateway-owned parameter names allowed as a SQL Server LIKE pattern (<see cref="CompileRequest.GatewayLikePatternParameters"/>).</summary>
    public IReadOnlySet<string> GatewayLikePatternParameters { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Rejects queries that reference masked columns inside WHERE/HAVING/ON/ORDER BY predicates.</summary>
    public bool RejectMaskedColumnsInPredicates { get; init; } = true;

    /// <summary>Rejects DML statements that target or read masked columns.</summary>
    public bool RejectMaskedColumnsInDml { get; init; } = true;

    /// <summary>Rejects UPDATE/DELETE statements without a WHERE clause.</summary>
    public bool RejectUnfilteredDml { get; init; } = true;

    /// <summary>Requires INSERT statements to explicitly supply the tenant isolation column.</summary>
    public bool RequireTenantColumnInInsert { get; init; } = true;

    /// <summary>Rejects UPDATE statements that attempt to modify the tenant isolation column.</summary>
    public bool DisallowTenantColumnModificationInUpdate { get; init; } = true;

    /// <summary>Rejects DML statements that attempt to assign policy-managed columns.</summary>
    public bool RejectPolicyColumnAssignment { get; init; } = true;

    /// <summary>Rejects INSERT statements targeting tables with active consent row filters.</summary>
    public bool RejectConsentFilteredInsert { get; init; } = true;

    /// <summary>Rejects whole-row table references inside DML statements.</summary>
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

    /// <summary>Enforces WITH CHECK OPTION semantics on DML writes against row-filtered tables.</summary>
    public bool EnforceWithCheckOption { get; init; } = true;

    /// <summary>Issue #277: a<c>WITH RECURSIVE</c> compiles only when true. Default false; DML rejects it regardless.</summary>
    public bool AllowRecursiveCte { get; init; }

    /// <summary>When true, the root pagination offset is emitted as a bind slot (ParameterOrigin.PageOffset).</summary>
    public bool BindPageOffset { get; init; }

    /// <summary>Target SQL dialect used to validate and normalize dialect-sensitive expressions.</summary>
    public TargetSqlDialect? TargetDialect { get; init; }

    /// <summary>Maximum rewritten pattern length ceiling.</summary>
    public int MaxQueryLength { get; init; } = 65_536;
}
