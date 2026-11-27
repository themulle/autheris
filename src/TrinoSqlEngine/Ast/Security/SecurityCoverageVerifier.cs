namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>The final tree does not prove complete policy coverage (INV-2, INV-3, INV-11). Fail closed; nothing is emitted.</summary>
public sealed class SecurityCoverageException : SecurityException
{
    public SecurityCoverageException(string message) : base(message)
    {
    }
}

/// <summary>
/// What the verifier expects for one physical table, independent of the injector (plan 3.2): the catalog canonical name and
/// the predicate ids that must appear in the WHERE of the secured derived table.
/// </summary>
/// <param name="Identity">Stable table identity used in <see cref="SecurityPredicateId.TableIdentity"/>.</param>
/// <param name="Schema">Catalog canonical schema name (exact case).</param>
/// <param name="Catalog">Optional catalog part (Unity Catalog); when set the canonical name has three parts.</param>
/// <param name="Table">Catalog canonical table name (exact case).</param>
/// <param name="RootPredicates">Predicates required when the table is referenced by the user query.</param>
/// <param name="PolicySubqueryPredicates">Predicates required when the table is referenced inside a policy subquery (tenant only).</param>
/// <param name="MaskedColumns">Canonical names of columns that must be projected as <see cref="MaskExpression"/> (never raw).</param>
/// <param name="CatalogColumns">Canonical names of the cataloged columns; a secured derived table may project only these (CR-ADG-06). Null: any named column.</param>
/// <param name="TenantColumn">Canonical tenant column (DML writes must carry the bound tenant there), or null.</param>
/// <param name="PolicyColumns">Lower-case names of the columns the applicable row-policy predicate references; an assignment to one of them is rejected.</param>
public sealed record PerCellColumnCoverage(
    bool CanBeClear,
    bool CanBeMasked,
    string? ClearConditionFingerprint,
    string? MaskConditionFingerprint);

public sealed record PerCellTableCoverage(
    bool AnySignatureUnfiltered,
    bool HasNarrowingPredicate,
    ImmutableDictionary<string, PerCellColumnCoverage> Columns,
    int ExpectedFlagJoinCount);

public sealed record TableCoverageRequirement(
    string Identity,
    string Schema,
    string Table,
    ImmutableArray<SecurityPredicateId> RootPredicates,
    ImmutableArray<SecurityPredicateId> PolicySubqueryPredicates,
    ImmutableHashSet<string>? MaskedColumns = null,
    string? Catalog = null,
    ImmutableHashSet<string>? CatalogColumns = null,
    string? TenantColumn = null,
    ImmutableHashSet<string>? PolicyColumns = null,
    ImmutableDictionary<string, string>? ColumnTypes = null,
    PerCellTableCoverage? PerCell = null,
    bool IsMetricView = false);

/// <summary>
/// Production post-condition of the compiler (runs on every compile). It walks the final AST and proves that every physical
/// table is the single source of a secured derived table whose WHERE contains the required <see cref="SecurityPredicateExpression"/>
/// ids as top-level conjuncts, that physical names are schema-qualified canonical names, that CTE names never hide a physical
/// table, and that policy subqueries carry the tenant predicate and do not nest. It works on resolved names and node
/// structure, never on text. It does not trust the injector: requirements come from <paramref name="requirementOf"/>.
/// </summary>
public sealed partial class SecurityCoverageVerifier
{
    private readonly Func<SqlQualifiedName, TableCoverageRequirement?> _requirementOf;
    private readonly TargetSqlDialect _dialect;
    private readonly int _maxSecuredTableReferences;
    private readonly int _maxAstDepth;
    private readonly DmlGuardOptions _dml;
    private readonly string? _tenantParameterName;
    private readonly DialectCapabilities? _capabilities;
    private readonly DmlWritePermit? _writePermit;
    private readonly PreExecutionRowCheck? _preExecutionRowCheck;

    public SecurityCoverageVerifier(
        Func<SqlQualifiedName, TableCoverageRequirement?> requirementOf,
        TargetSqlDialect dialect = TargetSqlDialect.SqlServer,
        int maxSecuredTableReferences = 256,
        int maxAstDepth = 512,
        DmlGuardOptions? dml = null,
        string? tenantParameterName = null,
        DialectCapabilities? capabilities = null,
        DmlWritePermit? writePermit = null,
        PreExecutionRowCheck? preExecutionRowCheck = null)
    {
        _capabilities = capabilities;
        _dml = dml ?? DmlGuardOptions.Strict;
        _tenantParameterName = tenantParameterName;
        _requirementOf = requirementOf ?? throw new ArgumentNullException(nameof(requirementOf));
        _dialect = dialect;
        _maxSecuredTableReferences = maxSecuredTableReferences;
        _maxAstDepth = maxAstDepth;
        _writePermit = writePermit;
        _preExecutionRowCheck = preExecutionRowCheck;
    }

    /// <summary>Verifies <paramref name="statement"/> and returns the root predicate ids found.</summary>
    public ImmutableArray<SecurityPredicateId> Verify(SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        cancellationToken.ThrowIfCancellationRequested();
        var walker = new Walker(this, cancellationToken);
        try
        {
            switch (statement)
            {
                case SelectStatement select:
                    walker.Select(select, ImmutableHashSet<string>.Empty, policyScope: false);
                    break;
                case InsertStatement insert:
                    walker.Insert(insert);
                    break;
                case UpdateStatement update:
                    walker.Update(update);
                    break;
                case DeleteStatement delete:
                    walker.Delete(delete);
                    break;
                case MergeStatement merge:
                    walker.Merge(merge);
                    break;
                default:
                    throw new SecurityCoverageException($"Statement '{statement.GetType().Name}' cannot be verified on the governed compiler path.");
            }
        }
        catch (InsufficientExecutionStackException)
        {
            throw new SqlLimitExceededException(SqlLimitKind.NestingDepth, _dialect, 0, 0);
        }

        return walker.Applied.ToImmutableArray();
    }

    private sealed partial class Walker(SecurityCoverageVerifier owner, CancellationToken ct)
    {
        private SecurityCoverageVerifier Owner => owner;

        private int _ticks;
        private int _depth;
        private int _insidePredicate;
        private int _securedReferences;
        public List<SecurityPredicateId> Applied { get; } = new();

        private void Enter()
        {
            ExecutionStackGuard.Ensure();
            if ((++_ticks & 255) == 0) ct.ThrowIfCancellationRequested();
            if (++_depth > owner._maxAstDepth)
            {
                throw new SqlLimitExceededException(SqlLimitKind.AstDepth, owner._dialect, _depth, owner._maxAstDepth);
            }
        }

        private void Exit() => _depth--;

        public void Select(SelectStatement s, ImmutableHashSet<string> cte, bool policyScope)
        {
            Enter();
            var scope = cte;
            if (s.With != null)
            {
                foreach (var c in s.With.Ctes)
                {
                    // Exit-timing: a CTE is visible in its own body only for WITH RECURSIVE; later CTEs and the body see it.
                    // CR-ADG-01: the definition must be a delimited identifier; references are compared to it exactly (ordinal),
                    // never re-folded, so the verifier cannot disagree with the database about which name binds.
                    if (!c.Name.IsQuoted)
                    {
                        throw new SecurityCoverageException("A CTE name is not emitted as a delimited identifier.");
                    }

                    var bodyScope = s.With.IsRecursive ? scope.Add(c.Name.Value) : scope;
                    Select(c.Query, bodyScope, policyScope);
                    scope = scope.Add(c.Name.Value);
                }
            }

            Body(s.Body, scope, policyScope, s);
            if (s.OrderBy != null)
            {
                foreach (var el in s.OrderBy.Elements) Expr(el.Expression, scope, policyScope);
            }

            if (s.Pagination != null)
            {
                if (s.Pagination.Offset != null) Expr(s.Pagination.Offset, scope, policyScope);
                if (s.Pagination.Limit != null) Expr(s.Pagination.Limit, scope, policyScope);
            }

            Exit();
        }

        private void Body(QueryBody body, ImmutableHashSet<string> cte, bool policyScope, SelectStatement? enclosing = null)
        {
            Enter();
            switch (body)
            {
                case QuerySpecification spec:
                    Spec(spec, cte, policyScope, enclosing);
                    break;
                case SetOperationQuery setOp:
                    Body(setOp.Left, cte, policyScope);
                    Body(setOp.Right, cte, policyScope);
                    break;
                case ValuesQueryBody values:
                    // A VALUES source contains no table and needs no predicate; it is recognized explicitly, not by omission.
                    foreach (var row in values.Rows) Expr(row, cte, policyScope);
                    break;
                default:
                    // TableQueryBody (TABLE t) and unknown bodies are never emitted: the injector rewrites TABLE t.
                    throw new SecurityCoverageException($"Query body '{body.GetType().Name}' is not covered by a security predicate.");
            }

            Exit();
        }

        private void Spec(QuerySpecification spec, ImmutableHashSet<string> cte, bool policyScope, SelectStatement? enclosing)
        {
            if (!policyScope) VerifyNoWholeRowOfMaskedSource(spec, cte);

            foreach (var item in spec.Projections)
            {
                if (item is ColumnSelectItem col) Expr(col.Expression, cte, policyScope);
            }

            if (spec.From is NamedTableSource named && !IsCte(named, cte))
            {
                Physical(named, null, spec, enclosing, policyScope);
            }
            else if (!policyScope && spec.From is JoinedTableSource && TryExtractFlagJoins(spec.From, cte, out var targetNamed, out var flagJoins))
            {
                Physical(targetNamed, flagJoins, spec, enclosing, policyScope);
            }
            else if (spec.From != null)
            {
                Source(spec.From, cte, policyScope);
            }

            if (spec.Where != null) Expr(spec.Where, cte, policyScope);
            if (spec.GroupBy != null)
            {
                foreach (var e in spec.GroupBy.GroupingExpressions) Expr(e, cte, policyScope);
                foreach (var el in spec.GroupBy.AdvancedElements ?? [])
                {
                    foreach (var set in el.Sets)
                    {
                        foreach (var e in set) Expr(e, cte, policyScope);
                    }
                }
            }

            if (spec.Having != null) Expr(spec.Having, cte, policyScope);
        }

        /// <summary>
        /// SR-L-24, second line of defence independent of the visitor: a projection must not reference the row of a secured derived
        /// table over a table with masked columns as a whole (<c>SELECT t FROM t</c>, <c>to_json(t)</c>), that is a one-part
        /// <see cref="ColumnReference"/> named like the derived table's alias that is not a cataloged column.
        /// </summary>
        private void VerifyNoWholeRowOfMaskedSource(QuerySpecification spec, ImmutableHashSet<string> cte)
        {
            if (spec.From is null) return;

            List<(string Alias, ImmutableHashSet<string>? CatalogColumns)>? aliases = null;
            CollectMaskedSources(spec.From, cte, ref aliases);
            if (aliases is null) return;

            foreach (var item in spec.Projections)
            {
                if (item is not ColumnSelectItem column) continue;

                bool wholeRow = false;
                AstReflection.Walk(column.Expression, node =>
                {
                    if (node is ColumnReference { Name.Parts.Count: 1 } reference)
                    {
                        foreach (var (alias, catalogColumns) in aliases)
                        {
                            if (string.Equals(reference.Name.SimpleName, alias, StringComparison.OrdinalIgnoreCase) &&
                                catalogColumns?.Contains(reference.Name.SimpleName) != true)
                            {
                                wholeRow = true;
                            }
                        }
                    }

                    return !wholeRow;
                });
                if (wholeRow)
                {
                    throw new SecurityCoverageException("A whole-row reference to a table with masked columns is projected.");
                }
            }
        }

        private void CollectMaskedSources(TableSource source, ImmutableHashSet<string> cte, ref List<(string Alias, ImmutableHashSet<string>? CatalogColumns)>? into)
        {
            switch (source)
            {
                case JoinedTableSource join:
                    CollectMaskedSources(join.Left, cte, ref into);
                    CollectMaskedSources(join.Right, cte, ref into);
                    break;
                case SubqueryTableSource { Alias: var alias, Subquery.Body: QuerySpecification { From: var innerFrom } }
                    when LeftmostNamedSource(innerFrom) is { } named && !IsCte(named, cte):
                    if (owner._requirementOf(named.Name) is { MaskedColumns.Count: > 0 } requirement)
                    {
                        (into ??= new()).Add((alias.Value, requirement.CatalogColumns));
                    }

                    break;
                case NamedTableSource named when !IsCte(named, cte): CollectMetricViewMaskedSource(named, ref into); break;
            }
        }

        private static NamedTableSource? LeftmostNamedSource(TableSource? source)
        {
            while (source is JoinedTableSource join)
            {
                source = join.Left;
            }

            return source as NamedTableSource;
        }

        private static bool TryExtractFlagJoins(
            TableSource source,
            ImmutableHashSet<string> cte,
            out NamedTableSource target,
            out List<(SubqueryTableSource Subquery, OnJoinCondition On)> joins)
        {
            target = null!;
            joins = new List<(SubqueryTableSource, OnJoinCondition)>();
            var stack = new Stack<(SubqueryTableSource, OnJoinCondition)>();
            var current = source;
            while (current is JoinedTableSource join)
            {
                if (join.Type != JoinType.LeftOuter ||
                    join.Right is not SubqueryTableSource sub ||
                    join.Condition is not OnJoinCondition on ||
                    !sub.Alias.Value.StartsWith(PerCellFlagDecomposition.FlagAliasPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                stack.Push((sub, on));
                current = join.Left;
            }

            if (current is not NamedTableSource named || IsCte(named, cte) || stack.Count == 0)
            {
                return false;
            }

            while (stack.Count > 0)
            {
                joins.Add(stack.Pop());
            }

            target = named;
            return true;
        }

        private static bool IsCte(NamedTableSource named, ImmutableHashSet<string> cte) =>
            named.Name.IsSimple && named.Name.Parts[0].IsQuoted && cte.Contains(named.Name.Parts[0].Value);

        private void Source(TableSource source, ImmutableHashSet<string> cte, bool policyScope)
        {
            Enter();
            switch (source)
            {
                case NamedTableSource named:
                    if (!IsCte(named, cte))
                    {
                        throw new SecurityCoverageException("A physical table is referenced outside a secured derived table.");
                    }

                    break;
                case SubqueryTableSource sub:
                    Select(sub.Subquery, cte, policyScope);
                    break;
                case LateralTableSource lateral:
                    Select(lateral.Subquery, cte, policyScope);
                    break;
                case JoinedTableSource join:
                    Source(join.Left, cte, policyScope);
                    Source(join.Right, cte, policyScope);
                    if (join.Condition is OnJoinCondition on) Expr(on.Predicate, cte, policyScope);
                    break;
                default:
                    throw new SecurityCoverageException($"Table source '{source.GetType().Name}' is not supported.");
            }

            Exit();
        }

        private void Physical(
            NamedTableSource table,
            List<(SubqueryTableSource Subquery, OnJoinCondition On)>? flagJoins,
            QuerySpecification spec,
            SelectStatement? enclosing,
            bool policyScope)
        {
            var where = spec.Where;
            var projections = spec.Projections;
            var requirement = owner._requirementOf(table.Name)
                ?? throw new SecurityCoverageException("A table reference could not be resolved against the catalog.");

            VerifyCanonicalName(table.Name, requirement);

            if (flagJoins is { Count: > 0 })
            {
                if (policyScope || requirement.PerCell is null || flagJoins.Count != requirement.PerCell.ExpectedFlagJoinCount)
                {
                    throw new SecurityCoverageException("A secured derived table contains unpermitted flag joins.");
                }

                VerifyFlagJoins(flagJoins, table.Alias?.Value, requirement);
            }
            else if (!policyScope && requirement.PerCell is { ExpectedFlagJoinCount: > 0 })
            {
                throw new SecurityCoverageException("A per-cell secured derived table is missing required flag joins.");
            }

            VerifyShape(requirement, spec, enclosing, policyScope);

            if (!policyScope && !requirement.IsMetricView && requirement.MaskedColumns is { Count: > 0 } masked)
            {
                VerifyMasks(masked, requirement.PerCell, projections);
            }

            var required = policyScope ? requirement.PolicySubqueryPredicates : requirement.RootPredicates;
            var present = new List<SecurityPredicateId>();
            Conjuncts(where, present);
            foreach (var id in required)
            {
                if (!present.Contains(id))
                {
                    throw new SecurityCoverageException("A secured table is missing a required security predicate in its derived-table WHERE.");
                }

                if (!policyScope && !Applied.Contains(id)) Applied.Add(id);
            }

            if (++_securedReferences > owner._maxSecuredTableReferences)
            {
                throw new SqlLimitExceededException(SqlLimitKind.SecuredTableReferences, owner._dialect, _securedReferences, owner._maxSecuredTableReferences);
            }
        }

        private void VerifyFlagJoins(
            List<(SubqueryTableSource Subquery, OnJoinCondition On)> flagJoins,
            string? targetAlias,
            TableCoverageRequirement requirement)
        {
            foreach (var (sub, on) in flagJoins)
            {
                if (sub.Subquery.With is not null || sub.Subquery.OrderBy is not null || sub.Subquery.Pagination is not null ||
                    sub.Subquery.Body is not QuerySpecification { Distinct: true, GroupBy: null, Having: null })
                {
                    throw new SecurityCoverageException("A per-cell flag join must be a plain SELECT DISTINCT subquery.");
                }

                VerifyFlagJoinOnPredicate(on.Predicate, sub.Alias.Value, targetAlias, requirement.CatalogColumns);
                Select(sub.Subquery, ImmutableHashSet<string>.Empty, policyScope: true);
            }
        }

        private static void VerifyFlagJoinOnPredicate(
            Expression predicate,
            string flagAlias,
            string? targetAlias,
            ImmutableHashSet<string>? catalogColumns)
        {
            var stack = new Stack<Expression>();
            stack.Push(predicate);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                while (cur is ParenthesizedExpression p) cur = p.Expression;

                if (cur is BinaryExpression { Operator: BinaryOperator.And } andExpr)
                {
                    stack.Push(andExpr.Right);
                    stack.Push(andExpr.Left);
                    continue;
                }

                if (cur is BinaryExpression { Operator: BinaryOperator.Equal, Left: ColumnReference left, Right: ColumnReference right } &&
                    left.Name.Parts.Count == 2 &&
                    string.Equals(left.Name.Parts[0].Value, flagAlias, StringComparison.Ordinal) &&
                    left.Name.Parts[1].Value.StartsWith(PerCellFlagDecomposition.FlagKeyPrefix, StringComparison.Ordinal) &&
                    right.Name.Parts.Count == 2 &&
                    targetAlias is not null &&
                    string.Equals(right.Name.Parts[0].Value, targetAlias, StringComparison.OrdinalIgnoreCase) &&
                    (catalogColumns is null || catalogColumns.Contains(right.Name.Parts[1].Value)))
                {
                    continue;
                }

                throw new SecurityCoverageException("A per-cell flag join ON condition must equate flag keys to target columns.");
            }
        }

        /// <summary>INV-11: the emitted name is the schema-qualified canonical catalog name, always delimited, never user spelling.</summary>
        private static void VerifyCanonicalName(SqlQualifiedName name, TableCoverageRequirement requirement)
        {
            var parts = name.Parts;
            int expectedParts = requirement.Catalog is null ? 2 : 3;
            int offset = expectedParts - 2;
            if (parts.Count != expectedParts || parts.Any(p => !p.IsQuoted) ||
                (requirement.Catalog is not null && !string.Equals(parts[0].Value, requirement.Catalog, StringComparison.Ordinal)) ||
                !string.Equals(parts[offset].Value, requirement.Schema, StringComparison.Ordinal) ||
                !string.Equals(parts[offset + 1].Value, requirement.Table, StringComparison.Ordinal))
            {
                throw new SecurityCoverageException("A physical table is not emitted as its schema-qualified canonical name.");
            }
        }

        /// <summary>
        /// CR-ADG-06: the physical table must be the single source of a pure secured derived table: it is the only body of its
        /// SELECT (no WITH, ORDER BY, pagination, set operation), there is no DISTINCT, GROUP BY or HAVING, the WHERE consists
        /// of security predicates only, and the projections are exactly cataloged columns or their mask expressions (no
        /// wildcard, no computed expression). A bare physical reference inside a policy subquery of a table without a tenant
        /// column has no predicate to carry and is exempt.
        /// </summary>
        private static void VerifyShape(TableCoverageRequirement requirement, QuerySpecification spec, SelectStatement? enclosing, bool policyScope)
        {
            if (requirement.IsMetricView) { VerifyMetricViewShape(requirement, spec, enclosing, policyScope); return; }
            var required = policyScope ? requirement.PolicySubqueryPredicates : requirement.RootPredicates;
            if (policyScope && required.IsEmpty)
            {
                return;
            }

            if (enclosing is null || !ReferenceEquals(enclosing.Body, spec) || enclosing.With is not null || enclosing.OrderBy is not null || enclosing.Pagination is not null)
            {
                throw new SecurityCoverageException("A physical table is not the single source of a secured derived table.");
            }

            if (spec.Distinct || spec.GroupBy is not null || spec.Having is not null)
            {
                throw new SecurityCoverageException("A secured derived table must not use DISTINCT, GROUP BY or HAVING.");
            }

            StrictConjuncts(spec.Where);

            foreach (var item in spec.Projections)
            {
                if (item is not ColumnSelectItem { Alias: { IsQuoted: true } alias } column)
                {
                    throw new SecurityCoverageException("A secured derived table may only project cataloged columns under their own name.");
                }

                if (!policyScope &&
                    requirement.PerCell is { } perCell &&
                    perCell.Columns.TryGetValue(alias.Value, out var colCov))
                {
                    VerifyPerCellColumnProjection(column.Expression, alias.Value, colCov, requirement);
                    continue;
                }

                var reference = column.Expression switch
                {
                    ColumnReference r => r,
                    MaskExpression m => m.Column,
                    _ => throw new SecurityCoverageException("A secured derived table may only project cataloged columns or their mask expressions.")
                };

                if (reference.Name.Parts.Count != 1 || !reference.Name.Parts[0].IsQuoted ||
                    !string.Equals(reference.Name.Parts[0].Value, alias.Value, StringComparison.Ordinal) ||
                    (requirement.CatalogColumns is not null && !requirement.CatalogColumns.Contains(alias.Value)))
                {
                    throw new SecurityCoverageException("A secured derived table projects a column that is not a cataloged column.");
                }
            }
        }

        private static void VerifyPerCellColumnProjection(
            Expression expression,
            string aliasName,
            PerCellColumnCoverage colCov,
            TableCoverageRequirement requirement)
        {
            if (requirement.CatalogColumns is not null && !requirement.CatalogColumns.Contains(aliasName))
            {
                throw new SecurityCoverageException("A secured derived table projects a column that is not a cataloged column.");
            }

            if (expression is not CaseExpression { Operand: null, ElseResult: LiteralExpression { Type: LiteralType.Null } } cs ||
                cs.WhenClauses.Count is < 1 or > 2)
            {
                throw new SecurityCoverageException("A row-dependent column must be projected as a CASE WHEN ... ELSE NULL expression.");
            }

            bool seenClear = false;
            bool seenMask = false;
            foreach (var when in cs.WhenClauses)
            {
                if (when.Condition is not SecurityPredicateExpression sp)
                {
                    throw new SecurityCoverageException("A row-dependent CASE branch condition must be a security predicate expression.");
                }

                string condFingerprint = AstReflection.Fingerprint(sp.Predicate);
                switch (when.Result)
                {
                    case ColumnReference r when !seenClear && colCov.CanBeClear:
                        if (r.Name.Parts.Count != 1 || !r.Name.Parts[0].IsQuoted ||
                            !string.Equals(r.Name.Parts[0].Value, aliasName, StringComparison.Ordinal) ||
                            !string.Equals(condFingerprint, colCov.ClearConditionFingerprint, StringComparison.Ordinal))
                        {
                            throw new SecurityCoverageException("A row-dependent clear branch does not match the authorized clear signature condition.");
                        }

                        seenClear = true;
                        break;
                    case MaskExpression m when !seenMask && colCov.CanBeMasked:
                        if (m.Column.Name.Parts.Count != 1 || !m.Column.Name.Parts[0].IsQuoted ||
                            !string.Equals(m.Column.Name.Parts[0].Value, aliasName, StringComparison.Ordinal) ||
                            !string.Equals(condFingerprint, colCov.MaskConditionFingerprint, StringComparison.Ordinal))
                        {
                            throw new SecurityCoverageException("A row-dependent mask branch does not match the authorized mask signature condition.");
                        }

                        seenMask = true;
                        break;
                    default:
                        throw new SecurityCoverageException("A row-dependent CASE branch contains an unauthorized expression.");
                }
            }

            if ((colCov.CanBeClear && !seenClear) || (colCov.CanBeMasked && !seenMask))
            {
                throw new SecurityCoverageException("A row-dependent CASE expression is missing an expected branch.");
            }
        }

        private static void StrictConjuncts(Expression? where)
        {
            if (where is null) return;
            var stack = new Stack<Expression>();
            stack.Push(where);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                while (current is ParenthesizedExpression p)
                {
                    current = p.Expression;
                }

                if (current is SecurityPredicateExpression)
                {
                    continue;
                }

                if (current is BinaryExpression { Operator: BinaryOperator.And } b)
                {
                    ExecutionStackGuard.Ensure();
                    stack.Push(b.Right);
                    stack.Push(b.Left);
                    continue;
                }

                throw new SecurityCoverageException("The WHERE of a secured derived table may only contain security predicates.");
            }
        }

        private static void VerifyMasks(ImmutableHashSet<string> masked, PerCellTableCoverage? perCell, IReadOnlyList<SelectItem> projections)
        {
            foreach (var item in projections)
            {
                if (item is not ColumnSelectItem column)
                {
                    throw new SecurityCoverageException("A table with masked columns is projected with a wildcard.");
                }

                if (column.Expression is MaskExpression mask)
                {
                    string outName = column.Alias?.Value ?? string.Empty;
                    if (!string.Equals(mask.Column.Name.SimpleName, outName, StringComparison.Ordinal))
                    {
                        throw new SecurityCoverageException("A mask expression is projected under a different column name.");
                    }

                    continue;
                }

                if (perCell is not null &&
                    column.Alias is { } colAlias &&
                    perCell.Columns.ContainsKey(colAlias.Value) &&
                    column.Expression is CaseExpression cs)
                {
                    foreach (var when in cs.WhenClauses)
                    {
                        if (ContainsRawMaskedColumn(when.Condition, masked))
                        {
                            throw new SecurityCoverageException("A masked column is referenced inside a CASE branch condition.");
                        }
                    }

                    continue;
                }

                if (ContainsRawMaskedColumn(column.Expression, masked))
                {
                    throw new SecurityCoverageException("A masked column is projected without its mask expression.");
                }
            }
        }

        private static bool ContainsRawMaskedColumn(SqlNode root, ImmutableHashSet<string> masked)
        {
            bool leaksRaw = false;
            AstReflection.Walk(root, node =>
            {
                if (node is MaskExpression) return false;
                if (node is ColumnReference reference && masked.Contains(reference.Name.SimpleName)) leaksRaw = true;
                return !leaksRaw;
            });
            return leaksRaw;
        }

        private static void Conjuncts(Expression? where, List<SecurityPredicateId> into)
        {
            if (where is null) return;
            var stack = new Stack<Expression>();
            stack.Push(where);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                while (current is ParenthesizedExpression p)
                {
                    current = p.Expression;
                }

                if (current is SecurityPredicateExpression sp)
                {
                    into.Add(sp.Id);
                }
                else if (current is BinaryExpression { Operator: BinaryOperator.And } b)
                {
                    ExecutionStackGuard.Ensure();
                    stack.Push(b.Right);
                    stack.Push(b.Left);
                }
            }
        }

        /// <summary>
        /// The typed policy builders emit <c>EXISTS (SELECT 1 FROM dep WHERE ...)</c>. The projection of an EXISTS cannot decide its outcome
        /// and carries no value of the policy, so exactly the integer 1 in that position is not a "raw literal value" (INV-5). The same
        /// constant anywhere else, any other literal, and every literal of the subquery's conditions are still checked. The check walks a copy
        /// in which the constant projection is a NULL; the verified tree itself is not changed.
        /// </summary>
        private static SelectStatement WithoutConstantProjection(SelectStatement subquery)
        {
            if (subquery.Body is not QuerySpecification spec ||
                !spec.Projections.Any(static p => p is ColumnSelectItem { Expression: LiteralExpression { Type: LiteralType.Integer, Value: { } v } } &&
                                                  Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) == 1))
            {
                return subquery;
            }

            var projections = spec.Projections
                .Select(static p => p is ColumnSelectItem { Expression: LiteralExpression { Type: LiteralType.Integer, Value: { } v } } column &&
                                    Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) == 1
                    ? column with { Expression = new LiteralExpression(null, LiteralType.Null) }
                    : p)
                .ToList();
            return subquery with { Body = spec with { Projections = projections } };
        }

        private static bool IsCanonicalConstant(BinaryExpression b) =>
            b.Left is LiteralExpression { Value: { } l } && b.Right is LiteralExpression { Value: { } r } &&
            Convert.ToInt64(l, System.Globalization.CultureInfo.InvariantCulture) == 1 &&
            Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture) is 0 or 1;

        private static bool ContainsSelect(Expression e)
        {
            var stack = new Stack<Expression>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case ExistsExpression or InSubqueryExpression or ScalarSubqueryExpression or QuantifiedComparisonExpression:
                        return true;
                    case BinaryExpression b:
                        stack.Push(b.Right);
                        stack.Push(b.Left);
                        break;
                    case ParenthesizedExpression p:
                        stack.Push(p.Expression);
                        break;
                    case UnaryExpression u:
                        stack.Push(u.Operand);
                        break;
                }
            }

            return false;
        }

        private void Expr(Expression e, ImmutableHashSet<string> cte, bool policyScope)
        {
            Enter();
            switch (e)
            {
                case SecurityPredicateExpression sp:
                    // Policy subqueries are one level deep: inside a policy only a plain tenant predicate may appear.
                    if (policyScope && (sp.Scope != SecurityScope.PolicySubquery || ContainsSelect(sp.Predicate)))
                    {
                        throw new SecurityCoverageException("Nested policy subqueries are not permitted.");
                    }

                    _insidePredicate++;
                    try
                    {
                        Expr(sp.Predicate, cte, policyScope: true);
                    }
                    finally
                    {
                        _insidePredicate--;
                    }

                    break;
                case NamedBindMarkerExpression:
                    throw new SecurityCoverageException("Text-rendering bind markers are not permitted on the typed compiler path.");
                case LiteralExpression literal:
                    // INV-5: injected predicates carry parameters, never raw values. NULL and booleans are keywords.
                    if (_insidePredicate > 0 && literal.Type is not (LiteralType.Null or LiteralType.Boolean))
                    {
                        throw new SecurityCoverageException("An injected security predicate contains a raw literal value.");
                    }

                    break;
                case TypedLiteralExpression or IntervalLiteralExpression or DateFunctionExpression when _insidePredicate > 0:
                    throw new SecurityCoverageException("An injected security predicate contains a raw literal value.");
                case MaskExpression:
                    break;
                case ColumnReference or ParameterReference or PolicyParameterExpression or PageOffsetParameterExpression or KeysetSeekParameterExpression
                    or TypedLiteralExpression or IntervalLiteralExpression or CurrentDateTimeExpression:
                    break;
                case ParenthesizedExpression p:
                    Expr(p.Expression, cte, policyScope);
                    break;
                case BinaryExpression { Operator: BinaryOperator.Equal, Left: LiteralExpression { Type: LiteralType.Integer }, Right: LiteralExpression { Type: LiteralType.Integer } } canonical
                    when _insidePredicate > 0 && IsCanonicalConstant(canonical):
                    break; // the canonical tautology and deny-all (1 = 1, 1 = 0)
                case BinaryExpression b:
                    if (Builder.SqlAstBuilder.CanUnrollLeftSpine(b.Operator)
                        && b.Left is BinaryExpression lb
                        && Builder.SqlAstBuilder.SameChainFamily(b.Operator, lb.Operator))
                    {
                        var spine = new List<BinaryExpression>();
                        Expression cur = b;
                        while (cur is BinaryExpression cb && Builder.SqlAstBuilder.SameChainFamily(b.Operator, cb.Operator))
                        {
                            if (spine.Count > 0)
                            {
                                ExecutionStackGuard.Ensure();
                                if ((++_ticks & 255) == 0) ct.ThrowIfCancellationRequested();
                            }

                            spine.Add(cb);
                            cur = cb.Left;
                        }

                        Expr(cur, cte, policyScope);
                        for (int i = spine.Count - 1; i >= 0; i--)
                        {
                            Expr(spine[i].Right, cte, policyScope);
                        }
                    }
                    else
                    {
                        Expr(b.Left, cte, policyScope);
                        Expr(b.Right, cte, policyScope);
                    }

                    break;
                case UnaryExpression u:
                    Expr(u.Operand, cte, policyScope);
                    break;
                case LikeExpression l:
                    Expr(l.Operand, cte, policyScope);
                    Expr(l.Pattern, cte, policyScope);
                    if (l.Escape != null) Expr(l.Escape, cte, policyScope);
                    break;
                case InListExpression inList:
                    Expr(inList.Operand, cte, policyScope);
                    foreach (var item in inList.Items) Expr(item, cte, policyScope);
                    break;
                case InSubqueryExpression inSub:
                    Expr(inSub.Operand, cte, policyScope);
                    Select(inSub.Subquery, cte, policyScope);
                    break;
                case ExistsExpression ex:
                    Select(WithoutConstantProjection(ex.Subquery), cte, policyScope);
                    break;
                case ScalarSubqueryExpression sc:
                    Select(sc.Subquery, cte, policyScope);
                    break;
                case QuantifiedComparisonExpression q:
                    Expr(q.Left, cte, policyScope);
                    Select(q.Subquery, cte, policyScope);
                    break;
                case IsDistinctFromExpression d:
                    Expr(d.Left, cte, policyScope);
                    Expr(d.Right, cte, policyScope);
                    break;
                case BetweenExpression bt:
                    Expr(bt.Operand, cte, policyScope);
                    Expr(bt.Lower, cte, policyScope);
                    Expr(bt.Upper, cte, policyScope);
                    break;
                case CaseExpression cs:
                    if (cs.Operand != null) Expr(cs.Operand, cte, policyScope);
                    foreach (var w in cs.WhenClauses)
                    {
                        Expr(w.Condition, cte, policyScope);
                        Expr(w.Result, cte, policyScope);
                    }

                    if (cs.ElseResult != null) Expr(cs.ElseResult, cte, policyScope);
                    break;
                case FunctionCallExpression fn:
                    foreach (var a in fn.Arguments) Expr(a, cte, policyScope);
                    if (fn.Filter != null) Expr(fn.Filter, cte, policyScope);
                    if (fn.OrderWithin != null)
                    {
                        foreach (var el in fn.OrderWithin.Elements) Expr(el.Expression, cte, policyScope);
                    }

                    if (fn.Window != null)
                    {
                        foreach (var pe in fn.Window.PartitionBy ?? []) Expr(pe, cte, policyScope);
                        if (fn.Window.OrderBy != null)
                        {
                            foreach (var el in fn.Window.OrderBy.Elements) Expr(el.Expression, cte, policyScope);
                        }
                    }

                    break;
                case CastExpression c:
                    Expr(c.Operand, cte, policyScope);
                    break;
                case RowValueExpression row:
                    foreach (var el in row.Elements) Expr(el, cte, policyScope);
                    break;
                case ArrayConstructorExpression arr:
                    foreach (var el in arr.Elements) Expr(el, cte, policyScope);
                    break;
                case SubscriptExpression sub:
                    Expr(sub.Target, cte, policyScope);
                    Expr(sub.Index, cte, policyScope);
                    break;
                case ExtractExpression ext:
                    Expr(ext.Source, cte, policyScope);
                    break;
                case SubstringExpression ss:
                    Expr(ss.Source, cte, policyScope);
                    Expr(ss.Start, cte, policyScope);
                    if (ss.Length != null) Expr(ss.Length, cte, policyScope);
                    break;
                case TrimExpression tr:
                    Expr(tr.Source, cte, policyScope);
                    if (tr.Characters != null) Expr(tr.Characters, cte, policyScope);
                    break;
                case PositionExpression pos:
                    Expr(pos.Needle, cte, policyScope);
                    Expr(pos.Haystack, cte, policyScope);
                    break;
                case GroupingOperationExpression g:
                    foreach (var gc in g.Columns) Expr(gc, cte, policyScope);
                    break;
                case DateFunctionExpression date:
                    Expr(date.Source, cte, policyScope);
                    break;
                default:
                    // Unknown expression node: fail closed (INV-1).
                    throw new SecurityCoverageException($"Expression node '{e.GetType().Name}' is not supported by the coverage verifier.");
            }

            Exit();
        }
    }
}
