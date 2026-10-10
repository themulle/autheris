using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Ast.Visitors;

// Typed DML injection (WP-A7). Every write goes through the catalog: the target and the written columns are emitted as canonical
// delimited names, the tenant of every written row is the gateway-bound tenant parameter (never request text), the tenant and
// policy predicates are opaque SecurityPredicateExpression conjuncts of the WHERE (UPDATE, DELETE) or of the ON (MERGE), and the
// user part is checked for masked-column reads, policy-column writes and unfiltered scope before anything is injected.
public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    public override SqlNode VisitMergeStatement(MergeStatement node)
    {
        if (_typed is null)
        {
            // MERGE exists only on the typed compiler path; the legacy string rewriter never supported it.
            throw new SecurityException("MERGE is only supported by the governed compiler.");
        }

        _rootLimitHandled = true;
        var entry = ResolveDmlTarget(node.Target);
        var alias = node.Target.Alias ?? new SqlIdentifier(node.Target.Name.SimpleName, node.Target.Name.Parts[^1].IsQuoted);
        var targetNames = new List<string> { alias.Value, entry.Identity.Table, node.Target.Name.SimpleName };
        var masked = MaskedColumnsOf(entry);

        // CR-ADG-33: isolation must never depend on a backend name-resolution error for a source that reuses the target's name.
        if (!MergeAliasGuard.IsDisjoint(node.Source, targetNames))
        {
            throw new SecurityException("A MERGE source alias must differ from the target alias and the target table name.");
        }

        // user part checks run on the user tree, before any injected predicate exists
        RejectMaskedReads(masked, targetNames, node.On);
        EnsureFilteredDml(node.On, "MERGE");

        var clauses = new List<MergeClause>(node.Clauses.Count);
        foreach (var clause in node.Clauses)
        {
            RejectMaskedReads(masked, targetNames, clause.Condition);
            var condition = clause.Condition != null ? (Expression)WithScope(SecurityScope.DmlSource, () => Visit(clause.Condition)) : null;
            switch (clause)
            {
                case MergeUpdateClause update:
                    clauses.Add(new MergeUpdateClause(condition, SecureAssignments(entry, update.Assignments, masked, targetNames)));
                    break;
                case MergeDeleteClause:
                    clauses.Add(new MergeDeleteClause(condition));
                    break;
                case MergeInsertClause insert:
                {
                    RejectInsertIntoPolicyTable(entry);
                    var columns = ResolveInsertColumns(entry, insert.Columns, masked, out int tenantIndex);
                    var values = ForceTenantInRow(entry, columns, insert.Values, tenantIndex);
                    RejectMaskedReads(masked, targetNames, values);
                    var visited = values.Select(v => (Expression)WithScope(SecurityScope.DmlSource, () => Visit(v))).ToList();
                    clauses.Add(new MergeInsertClause(condition, columns.Columns, visited));
                    break;
                }
                default:
                    // The clause set is closed (SEC-ADG-08 a).
                    throw new SecurityException($"MERGE clause '{clause.GetType().Name}' is not supported.");
            }
        }

        var source = (TableSource)WithScope(SecurityScope.MergeSource, () => Visit(node.Source));
        var on = (Expression)WithScope(SecurityScope.DmlSource, () => Visit(node.On));
        var combinedOn = AndInjected(on, BuildTargetPredicates(entry, SecurityScope.MergeOn, alias));
        return new MergeStatement(new NamedTableSource(entry.Identity.ToQualifiedName(), alias), source, combinedOn!, clauses);
    }

    private SqlNode SecureDeleteTyped(DeleteStatement node)
    {
        _rootLimitHandled = true;
        var entry = ResolveDmlTarget(node.TargetTable);
        var masked = MaskedColumnsOf(entry);
        var targetNames = new List<string> { entry.Identity.Table, node.TargetTable.Name.SimpleName };

        RejectMaskedReads(masked, targetNames, node.Where);
        EnsureFilteredDml(node.Where, "DELETE");
        var where = node.Where != null ? (Expression)WithScope(SecurityScope.DmlSource, () => Visit(node.Where)) : null;
        var combined = AndInjected(where, BuildTargetPredicates(entry, SecurityScope.DmlTarget, null));
        return new DeleteStatement(new NamedTableSource(entry.Identity.ToQualifiedName(), null), combined);
    }

    private SqlNode SecureUpdateTyped(UpdateStatement node)
    {
        _rootLimitHandled = true;
        var entry = ResolveDmlTarget(node.TargetTable);
        var masked = MaskedColumnsOf(entry);
        var targetNames = new List<string> { entry.Identity.Table, node.TargetTable.Name.SimpleName };

        var assignments = SecureAssignments(entry, node.Assignments, masked, targetNames);
        RejectMaskedReads(masked, targetNames, node.Where);
        EnsureFilteredDml(node.Where, "UPDATE");
        var where = node.Where != null ? (Expression)WithScope(SecurityScope.DmlSource, () => Visit(node.Where)) : null;
        var combined = AndInjected(where, BuildTargetPredicates(entry, SecurityScope.DmlTarget, null));
        return new UpdateStatement(new NamedTableSource(entry.Identity.ToQualifiedName(), null), assignments, combined);
    }

    private SqlNode SecureInsertTyped(InsertStatement node)
    {
        _rootLimitHandled = true;
        var entry = ResolveDmlTarget(node.TargetTable);
        RejectInsertIntoPolicyTable(entry);
        var masked = MaskedColumnsOf(entry);
        var columns = ResolveInsertColumns(entry, node.Columns, masked, out int tenantIndex);
        var forced = ForceTenantInSource(entry, columns, node.Source, tenantIndex);
        var source = (QueryBody)WithScope(SecurityScope.DmlSource, () => Visit(forced));
        return new InsertStatement(new NamedTableSource(entry.Identity.ToQualifiedName(), null), columns.Columns, source);
    }

    // ---- target, columns, assignments ----

    private TableCatalogEntry ResolveDmlTarget(NamedTableSource target)
    {
        // SEC-ADG-07: a DML target is never a CTE (CTE names are not catalog tables; they are rejected, not skipped).
        if (IsCte(target.Name))
        {
            throw new SecurityException("A DML target must be a catalog table, not a CTE.");
        }

        var entry = _typed!.Catalog.Resolve(target.Name)
            ?? throw new SecurityException("A DML target could not be resolved against the catalog.");
        _typed.RecordDependency(target.Name, entry);
        return entry;
    }

    private HashSet<string> MaskedColumnsOf(TableCatalogEntry entry) =>
        entry.Columns.Where(c => _typed!.Masks.HasMask(entry.Identity, c.Name)).Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void RejectMaskedReads(ISet<string> masked, ICollection<string> targetNames, params object?[] parts)
    {
        if (!_options.RejectMaskedColumnsInDml) return;
        if (DmlMaskedReadGuard.FindViolation(masked, targetNames, parts) is { } violation)
        {
            throw new SecurityException(violation);
        }
    }

    private static CatalogColumn ResolveColumn(TableCatalogEntry entry, SqlIdentifier written)
    {
        CatalogColumn? match = null;
        foreach (var column in entry.Columns)
        {
            if (!string.Equals(column.Name, written.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null)
            {
                throw new SecurityException("A DML column name is ambiguous in the catalog.");
            }

            match = column;
        }

        return match ?? throw new SecurityException("A DML column is not a cataloged column of the target table.");
    }

    private bool IsTenantColumn(TableCatalogEntry entry, CatalogColumn column) =>
        entry.TenantColumn is not null && string.Equals(column.Name, entry.TenantColumn, StringComparison.Ordinal);

    /// <summary>The gateway-bound tenant as a value of a written row (INV-10); never request text.</summary>
    private PolicyParameterExpression TenantValue(TableCatalogEntry entry)
    {
        var typed = _typed!;
        typed.AddValue(typed.Tenant.ParameterName, new PolicyValue(typed.Tenant.Value, typed.Tenant.Type));
        var column = entry.Columns.First(c => string.Equals(c.Name, entry.TenantColumn, StringComparison.Ordinal));
        return new PolicyParameterExpression(typed.Tenant.ParameterName, typed.Tenant.Type, ParameterOrigin.Tenant, ColumnType: column.DataType);
    }

    /// <summary>Verifies that a user-supplied tenant value is the caller's tenant; the message carries no value (INV-16).</summary>
    private void EnsureCallerTenantLiteral(Expression value, string operation)
    {
        if (value is not LiteralExpression { Value: not null } literal)
        {
            throw new SecurityException($"The tenant column in {operation} must be a literal equal to the caller's tenant.");
        }

        string given = Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        string expected = Convert.ToString(_typed!.Tenant.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(given, expected, StringComparison.Ordinal))
        {
            throw new SecurityException($"The tenant column value in {operation} does not match the caller's tenant.");
        }
    }

    private void RejectInsertIntoPolicyTable(TableCatalogEntry entry)
    {
        if (!_options.RejectConsentFilteredInsert) return;
        var id = entry.Identity;
        if (_typed!.RowFilters.ShouldApplyPolicy(id) ||
            _options.TablesWithConsentRowFilter.Contains(id.ToString()) || _options.TablesWithConsentRowFilter.Contains(id.Table))
        {
            // SQ-07: the written row cannot be verified against the row policy, so a table with a policy is not writable by INSERT.
            throw new SecurityException("INSERT into a table with a row-level policy is not permitted.");
        }
    }

    private sealed record InsertColumns(IReadOnlyList<SqlIdentifier> Columns, int OriginalCount, bool TenantAppended);

    private InsertColumns ResolveInsertColumns(TableCatalogEntry entry, IReadOnlyList<SqlIdentifier>? written, ISet<string> masked, out int tenantIndex)
    {
        if (written is null || written.Count == 0)
        {
            throw new SecurityException("A governed INSERT must list its target columns.");
        }

        var resolved = new List<SqlIdentifier>(written.Count + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        tenantIndex = -1;
        foreach (var id in written)
        {
            var column = ResolveColumn(entry, id);
            if (!seen.Add(column.Name))
            {
                throw new SecurityException("A column is listed more than once.");
            }

            if (_options.RejectMaskedColumnsInDml && masked.Contains(column.Name))
            {
                throw new SecurityException("A masked column must not be written.");
            }

            if (IsTenantColumn(entry, column)) tenantIndex = resolved.Count;
            resolved.Add(new SqlIdentifier(column.Name, IsQuoted: true));
        }

        bool appended = false;
        if (entry.TenantColumn is not null && tenantIndex < 0)
        {
            if (_options.RequireTenantColumnInInsert)
            {
                throw new SecurityException("The tenant column must be explicitly specified in an INSERT statement.");
            }

            tenantIndex = resolved.Count;
            resolved.Add(new SqlIdentifier(entry.TenantColumn, IsQuoted: true));
            appended = true;
        }

        return new InsertColumns(resolved, written.Count, appended);
    }

    private IReadOnlyList<Expression> ForceTenantInRow(TableCatalogEntry entry, InsertColumns columns, IReadOnlyList<Expression> row, int tenantIndex)
    {
        if (row.Count != columns.OriginalCount)
        {
            throw new SecurityException("The number of inserted values does not match the column list.");
        }

        if (tenantIndex < 0 || entry.TenantColumn is null) return row;
        var result = row.ToList();
        if (columns.TenantAppended)
        {
            result.Add(TenantValue(entry));
        }
        else
        {
            EnsureCallerTenantLiteral(result[tenantIndex], "INSERT");
            result[tenantIndex] = TenantValue(entry);
        }

        return result;
    }

    private QueryBody ForceTenantInSource(TableCatalogEntry entry, InsertColumns columns, QueryBody source, int tenantIndex)
    {
        switch (source)
        {
            case ValuesQueryBody values:
                if (values.Rows.Count == 0)
                {
                    throw new SecurityException("An INSERT VALUES clause without rows cannot be verified.");
                }

                return values with { Rows = values.Rows.Select(r => new RowValueExpression(ForceTenantInRow(entry, columns, r.Elements, tenantIndex))).ToList() };
            case QuerySpecification spec:
            {
                if (spec.Projections.Count != columns.OriginalCount || spec.Projections.Any(p => p is not ColumnSelectItem))
                {
                    throw new SecurityException("An INSERT SELECT must project exactly its listed columns (no wildcard).");
                }

                if (tenantIndex < 0 || entry.TenantColumn is null) return spec;
                var projections = spec.Projections.ToList();
                if (columns.TenantAppended)
                {
                    projections.Add(new ColumnSelectItem(TenantValue(entry), null));
                }
                else
                {
                    var item = (ColumnSelectItem)projections[tenantIndex];
                    EnsureCallerTenantLiteral(item.Expression, "INSERT SELECT");
                    projections[tenantIndex] = item with { Expression = TenantValue(entry) };
                }

                return spec with { Projections = projections };
            }
            case SetOperationQuery setOp:
                return setOp with
                {
                    Left = ForceTenantInSource(entry, columns, setOp.Left, tenantIndex),
                    Right = ForceTenantInSource(entry, columns, setOp.Right, tenantIndex)
                };
            default:
                throw new SecurityException("The INSERT source shape cannot be verified against the tenant check option.");
        }
    }

    private IReadOnlyList<UpdateAssignment> SecureAssignments(
        TableCatalogEntry entry,
        IReadOnlyList<UpdateAssignment> assignments,
        ISet<string> masked,
        ICollection<string> targetNames)
    {
        var typed = _typed!;
        var policyColumns = _options.RejectPolicyColumnAssignment && typed.RowFilters.ShouldApplyPolicy(entry.Identity)
            ? typed.RowFilters.GetPredicate(entry.Identity).ReferencedColumns
            : System.Collections.Immutable.ImmutableHashSet<string>.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UpdateAssignment>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var column = ResolveColumn(entry, assignment.Column);
            if (!seen.Add(column.Name))
            {
                throw new SecurityException("A column is assigned more than once.");
            }

            if (_options.RejectMaskedColumnsInDml && masked.Contains(column.Name))
            {
                throw new SecurityException("A masked column must not be written.");
            }

            if (policyColumns.Contains(column.Name.ToLowerInvariant()))
            {
                // SEC-ADG-06: an assignment to a policy column would move the row across the policy boundary.
                throw new SecurityException("A column referenced by the row policy must not be assigned.");
            }

            RejectMaskedReads(masked, targetNames, assignment.Value);
            Expression value;
            if (IsTenantColumn(entry, column))
            {
                if (_options.DisallowTenantColumnModificationInUpdate)
                {
                    throw new SecurityException("The tenant column must not be modified by an UPDATE statement.");
                }

                EnsureCallerTenantLiteral(assignment.Value, "UPDATE");
                value = TenantValue(entry);
            }
            else
            {
                value = (Expression)WithScope(SecurityScope.DmlSource, () => Visit(assignment.Value));
            }

            result.Add(new UpdateAssignment(new SqlIdentifier(column.Name, IsQuoted: true), value));
        }

        return result;
    }

    // ---- injected predicates ----

    private List<Expression> BuildTargetPredicates(TableCatalogEntry entry, SecurityScope scope, SqlIdentifier? qualifier)
    {
        var typed = _typed!;
        var tid = entry.Identity;
        var predicates = new List<Expression>(2);
        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant != null)
        {
            predicates.Add(new SecurityPredicateExpression(Qualify(tenant, qualifier), new SecurityPredicateId(tid.ToString(), 0), scope));
        }

        if (typed.RowFilters.ShouldApplyPolicy(tid))
        {
            var predicate = typed.RowFilters.GetPredicate(tid);
            typed.AddValues(predicate.Parameters);
            var expression = (Expression)new PolicySubqueryTenantRewriter(typed).Visit(predicate.Expression);
            expression = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(expression);
            bool referencesTarget = AstReflection.Collect<ColumnReference>(expression).Any(c =>
                c.Name.Parts.Count >= 2 && string.Equals(c.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
            if (referencesTarget)
            {
                // SEC-ADG-06 item 2: a correlated row filter cannot be bound to a DML target.
                throw new SecurityException("Correlated row filters are not supported for UPDATE, DELETE and MERGE statements.");
            }

            predicates.Add(new SecurityPredicateExpression(Qualify(expression, qualifier), new SecurityPredicateId(tid.ToString(), 1), scope));
        }

        return predicates;
    }

    private static Expression Qualify(Expression predicate, SqlIdentifier? qualifier) =>
        qualifier is null ? predicate : (Expression)new TargetColumnQualifier { Qualifier = qualifier }.Visit(predicate);

    /// <summary>The user's part first (parenthesized, so an OR cannot absorb the injected predicates), then every injected predicate.</summary>
    private static Expression? AndInjected(Expression? userPart, IReadOnlyList<Expression> injected)
    {
        Expression? result = userPart is null ? null : new ParenthesizedExpression(userPart);
        foreach (var predicate in injected)
        {
            result = result is null ? predicate : new BinaryExpression(result, BinaryOperator.And, predicate);
        }

        return result;
    }

    /// <summary>Qualifies the unqualified column references of a predicate that belong to the target; nested SELECTs are left alone.</summary>
    private sealed class TargetColumnQualifier : SqlAstRewriter
    {
        public SqlIdentifier Qualifier { get; init; } = new("t");

        public override SqlNode VisitSelectStatement(SelectStatement node) => node;

        public override SqlNode VisitColumnReference(ColumnReference node) =>
            node.Name.Parts.Count == 1
                ? new ColumnReference(new SqlQualifiedName(new[] { Qualifier, node.Name.Parts[0] }))
                : node;
    }
}
