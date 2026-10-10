namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

// DML post-conditions (WP-A7, INV-2, INV-10, SEC-ADG-06, SEC-ADG-08). The verifier does not trust the injector: it proves on the
// final tree that every DML target carries its required security predicates as top-level conjuncts, that every write carries the
// gateway-bound tenant, that masked and policy columns are never written, and that masked columns are never read by the user part.
public sealed partial class SecurityCoverageVerifier
{
    private sealed partial class Walker
    {
        private bool IsTenantParameter(Expression? expression) =>
            expression is PolicyParameterExpression { Origin: ParameterOrigin.Tenant } parameter &&
            Owner._tenantParameterName is { } name && string.Equals(parameter.Name, name, StringComparison.Ordinal);

        private TableCoverageRequirement RequireTarget(NamedTableSource target)
        {
            var requirement = Owner._requirementOf(target.Name)
                ?? throw new SecurityCoverageException("A DML target could not be resolved against the catalog.");
            VerifyCanonicalName(target.Name, requirement);
            return requirement;
        }

        private void CountSecuredReference()
        {
            if (++_securedReferences > Owner._maxSecuredTableReferences)
            {
                throw new SqlLimitExceededException(SqlLimitKind.SecuredTableReferences, Owner._dialect, _securedReferences, Owner._maxSecuredTableReferences);
            }
        }

        /// <summary>The required predicates must be top-level conjuncts of <paramref name="where"/> with the DML scope.</summary>
        private void RequirePredicates(TableCoverageRequirement requirement, Expression? where, SecurityScope scope)
        {
            var present = new List<SecurityPredicateExpression>();
            ConjunctNodes(where, present);
            foreach (var id in requirement.RootPredicates)
            {
                if (!present.Any(p => p.Id == id && p.Scope == scope))
                {
                    throw new SecurityCoverageException("A DML target is missing a required security predicate.");
                }

                if (!Applied.Contains(id)) Applied.Add(id);
            }

            CountSecuredReference();
        }

        private static void ConjunctNodes(Expression? where, List<SecurityPredicateExpression> into)
        {
            switch (where)
            {
                case null:
                    return;
                case SecurityPredicateExpression sp:
                    into.Add(sp);
                    return;
                case ParenthesizedExpression p:
                    ConjunctNodes(p.Expression, into);
                    return;
                case BinaryExpression { Operator: BinaryOperator.And } b:
                    RuntimeHelpers.EnsureSufficientExecutionStack();
                    ConjunctNodes(b.Left, into);
                    ConjunctNodes(b.Right, into);
                    return;
            }
        }

        /// <summary>
        /// The user part of a DML statement must not read a masked column of the target (no copy-out oracle, SEC-ADG-08 b). Injected
        /// predicates are skipped: they may reference masked columns of the table they secure.
        /// </summary>
        private static void NoMaskedReads(TableCoverageRequirement requirement, ICollection<string> targetNames, params object?[] userParts)
        {
            if (requirement.MaskedColumns is not { Count: > 0 } masked) return;
            var maskedNames = new HashSet<string>(masked, StringComparer.OrdinalIgnoreCase);
            if (DmlMaskedReadGuard.FindViolation(maskedNames, targetNames, userParts) is { } violation)
            {
                throw new SecurityCoverageException(violation);
            }
        }

        /// <summary>Validates assignment targets: cataloged, delimited, unique, not masked, not the tenant column, not a policy column.</summary>
        private void VerifyAssignments(TableCoverageRequirement requirement, IReadOnlyList<UpdateAssignment> assignments)
        {
            if (assignments.Count == 0)
            {
                throw new SecurityCoverageException("An UPDATE without assignments cannot be verified.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assignment in assignments)
            {
                string column = RequireCatalogColumn(requirement, assignment.Column);
                if (!seen.Add(column))
                {
                    throw new SecurityCoverageException("A column is assigned more than once.");
                }

                if (requirement.MaskedColumns?.Contains(column) == true && Owner._dml.RejectMaskedColumnsInDml)
                {
                    throw new SecurityCoverageException("A masked column is written by the statement.");
                }

                if (requirement.TenantColumn is not null && string.Equals(column, requirement.TenantColumn, StringComparison.Ordinal))
                {
                    // A tenant assignment is only ever the bound tenant (never a user value) and only when the options allow it.
                    if (Owner._dml.DisallowTenantColumnModificationInUpdate || !IsTenantParameter(assignment.Value))
                    {
                        throw new SecurityCoverageException("The tenant column is assigned by the statement.");
                    }
                }

                if (Owner._dml.RejectPolicyColumnAssignment && requirement.PolicyColumns?.Contains(column.ToLowerInvariant()) == true)
                {
                    throw new SecurityCoverageException("A column referenced by the row policy is assigned by the statement.");
                }
            }
        }

        private static string RequireCatalogColumn(TableCoverageRequirement requirement, SqlIdentifier column)
        {
            if (!column.IsQuoted || (requirement.CatalogColumns is not null && !requirement.CatalogColumns.Contains(column.Value)))
            {
                throw new SecurityCoverageException("A DML column is not a cataloged column emitted as a delimited identifier.");
            }

            return column.Value;
        }

        /// <summary>Insert columns: cataloged, unique, not masked; the tenant column is present when the table has one.</summary>
        private int VerifyInsertColumns(TableCoverageRequirement requirement, IReadOnlyList<SqlIdentifier>? columns, bool allowCheckOption = false)
        {
            if (columns is null || columns.Count == 0)
            {
                throw new SecurityCoverageException("A governed INSERT must list its target columns.");
            }

            if (!allowCheckOption && Owner._dml.RejectConsentFilteredInsert && requirement.RootPredicates.Any(p => p.Ordinal == 1))
            {
                throw new SecurityCoverageException("An INSERT into a table with a row policy cannot be verified against the policy.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int tenantIndex = -1;
            for (int i = 0; i < columns.Count; i++)
            {
                string name = RequireCatalogColumn(requirement, columns[i]);
                if (!seen.Add(name))
                {
                    throw new SecurityCoverageException("A column is listed more than once.");
                }

                if (requirement.MaskedColumns?.Contains(name) == true && Owner._dml.RejectMaskedColumnsInDml)
                {
                    throw new SecurityCoverageException("A masked column is written by the statement.");
                }

                if (requirement.TenantColumn is not null && string.Equals(name, requirement.TenantColumn, StringComparison.Ordinal))
                {
                    tenantIndex = i;
                }
            }

            if (requirement.TenantColumn is not null && tenantIndex < 0)
            {
                throw new SecurityCoverageException("An INSERT does not carry the bound tenant.");
            }

            return tenantIndex;
        }

        private void VerifyInsertSourceShape(QueryBody source, int columnCount, int tenantIndex)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            switch (source)
            {
                case ValuesQueryBody values:
                    if (values.Rows.Count == 0)
                    {
                        throw new SecurityCoverageException("An INSERT VALUES clause without rows cannot be verified.");
                    }

                    foreach (var row in values.Rows)
                    {
                        if (row.Elements.Count != columnCount || (tenantIndex >= 0 && !IsTenantParameter(row.Elements[tenantIndex])))
                        {
                            throw new SecurityCoverageException("An INSERT row does not carry the bound tenant.");
                        }
                    }

                    break;
                case QuerySpecification spec:
                    if (spec.Projections.Count != columnCount || spec.Projections.Any(p => p is not ColumnSelectItem))
                    {
                        throw new SecurityCoverageException("An INSERT SELECT projection cannot be verified (wildcard or column count).");
                    }

                    if (tenantIndex >= 0 && !IsTenantParameter(((ColumnSelectItem)spec.Projections[tenantIndex]).Expression))
                    {
                        throw new SecurityCoverageException("An INSERT SELECT does not carry the bound tenant.");
                    }

                    break;
                case SetOperationQuery setOp:
                    VerifyInsertSourceShape(setOp.Left, columnCount, tenantIndex);
                    VerifyInsertSourceShape(setOp.Right, columnCount, tenantIndex);
                    break;
                default:
                    throw new SecurityCoverageException("An INSERT source shape cannot be verified against the tenant check option.");
            }
        }

        public void Insert(InsertStatement insert)
        {
            Enter();
            if (insert.TargetTable.Alias is not null)
            {
                throw new SecurityCoverageException("An INSERT target must not have an alias.");
            }

            var requirement = RequireTarget(insert.TargetTable);
            var policyIds = requirement.RootPredicates.Where(p => p.Ordinal == 1).ToList();
            int tenantIndex = VerifyInsertColumns(requirement, insert.Columns, allowCheckOption: true);
            Body(insert.Source, ImmutableHashSet<string>.Empty, policyScope: false);
            if (policyIds.Count > 0)
            {
                // CR-ADG-35: a table with a row policy accepts only the check-option shape, with the policy over the inserted values.
                VerifyCheckOptionSource(insert.Source, insert.Columns!, tenantIndex, policyIds, requirement);
                foreach (var id in policyIds)
                {
                    if (!Applied.Contains(id)) Applied.Add(id);
                }
            }
            else
            {
                VerifyInsertSourceShape(insert.Source, insert.Columns!.Count, tenantIndex);
            }

            CountSecuredReference();
            Exit();
        }

        /// <summary>
        /// <c>SELECT v.c1, ... FROM (SELECT row UNION ALL SELECT row ...) v WHERE &lt;policy over v&gt;</c>: the policy is a top-level conjunct
        /// with the check scope, every row carries the bound tenant, the projection is exactly the listed columns of the derived table.
        /// </summary>
        private void VerifyCheckOptionSource(QueryBody source, IReadOnlyList<SqlIdentifier> columns, int tenantIndex, List<SecurityPredicateId> policyIds, TableCoverageRequirement requirement)
        {
            if (source is not QuerySpecification { Distinct: false, GroupBy: null, Having: null, From: SubqueryTableSource derived } spec ||
                !string.Equals(derived.Alias.Value, Visitors.AstSecurityVisitor.InsertCheckAlias.Value, StringComparison.Ordinal) ||
                derived.ColumnAliases is not null ||
                derived.Subquery is not { With: null, OrderBy: null, Pagination: null } inner)
            {
                throw new SecurityCoverageException("An INSERT into a table with a row policy does not have the check-option shape.");
            }

            if (spec.Projections.Count != columns.Count)
            {
                throw new SecurityCoverageException("An INSERT check-option projection does not match the column list.");
            }

            for (int i = 0; i < columns.Count; i++)
            {
                if (spec.Projections[i] is not ColumnSelectItem { Expression: ColumnReference { Name.Parts.Count: 2 } reference } ||
                    !string.Equals(reference.Name.Parts[0].Value, derived.Alias.Value, StringComparison.Ordinal) ||
                    !string.Equals(reference.Name.Parts[1].Value, columns[i].Value, StringComparison.Ordinal))
                {
                    throw new SecurityCoverageException("An INSERT check-option projection does not match the column list.");
                }
            }

            var present = new List<SecurityPredicateExpression>();
            ConjunctNodes(spec.Where, present);
            foreach (var id in policyIds)
            {
                var check = present.FirstOrDefault(p => p.Id == id && p.Scope == SecurityScope.InsertCheck)
                    ?? throw new SecurityCoverageException("An INSERT into a table with a row policy is missing its check predicate.");
                VerifyCheckStringComparisons(check.Predicate, derived.Alias, columns, tenantIndex, requirement);
            }

            // CR-ADG-42: every row value except the bound tenant is cast to the catalog type of its column.
            var nativeTypes = new string?[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == tenantIndex) continue;
                if (requirement.ColumnTypes is null || !requirement.ColumnTypes.TryGetValue(columns[i].Value, out string? catalogType) ||
                    !CatalogTypeMap.TryResolve(Owner._dialect, catalogType, out string native, out _))
                {
                    throw new SecurityCoverageException("An INSERT check-option column has no known catalog type to cast to.");
                }

                nativeTypes[i] = native;
            }

            VerifyCheckOptionRows(inner.Body, columns.Count, tenantIndex, nativeTypes);
        }

        /// <summary>
        /// CR-ADG-42: a string column of the inserted values is compared only with the byte-exact equality of the dialect (the
        /// verifier rebuilds the expected comparison through the factory and compares structure). Range, LIKE, negation and
        /// functions over a string column have no accepted shape.
        /// </summary>
        private void VerifyCheckStringComparisons(Expression predicate, SqlIdentifier alias, IReadOnlyList<SqlIdentifier> columns, int tenantIndex, TableCoverageRequirement requirement)
        {
            var textColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++)
            {
                if (i == tenantIndex) continue;
                if (requirement.ColumnTypes is not null && requirement.ColumnTypes.TryGetValue(columns[i].Value, out string? type) &&
                    CatalogTypeMap.TryResolve(Owner._dialect, type, out _, out bool isText) && isText)
                {
                    textColumns.Add(columns[i].Value);
                }
            }

            var capabilities = Owner._capabilities ?? DialectCapabilityTable.Default.Get(Owner._dialect);
            try
            {
                InsertCheckPolicy.Validate(predicate, alias, textColumns.Contains, capabilities);
            }
            catch (InsertCheckPolicy.UnsupportedStringPredicateException)
            {
                throw new SecurityCoverageException("An INSERT check compares a string column otherwise than byte-exact.");
            }
        }

        private void VerifyCheckOptionRows(QueryBody body, int columnCount, int tenantIndex, string?[] nativeTypes)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            switch (body)
            {
                case SetOperationQuery { Operator: SetOperator.Union, Distinct: false } setOp:
                    VerifyCheckOptionRows(setOp.Left, columnCount, tenantIndex, nativeTypes);
                    VerifyCheckOptionRows(setOp.Right, columnCount, tenantIndex, nativeTypes);
                    break;
                case QuerySpecification { Distinct: false, From: null, Where: null, GroupBy: null, Having: null } row:
                    if (row.Projections.Count != columnCount || row.Projections.Any(p => p is not ColumnSelectItem))
                    {
                        throw new SecurityCoverageException("An INSERT check-option row does not match the column list.");
                    }

                    if (tenantIndex >= 0 && !IsTenantParameter(((ColumnSelectItem)row.Projections[tenantIndex]).Expression))
                    {
                        throw new SecurityCoverageException("An INSERT row does not carry the bound tenant.");
                    }

                    for (int i = 0; i < columnCount; i++)
                    {
                        if (i == tenantIndex) continue;
                        if (((ColumnSelectItem)row.Projections[i]).Expression is not CastExpression { IsNativeType: true, IsTryCast: false } cast ||
                            !string.Equals(cast.TargetType, nativeTypes[i], StringComparison.Ordinal))
                        {
                            throw new SecurityCoverageException("An INSERT check-option value is not cast to the catalog type of its column.");
                        }
                    }

                    break;
                default:
                    throw new SecurityCoverageException("An INSERT check-option source shape cannot be verified.");
            }
        }

        public void Update(UpdateStatement update)
        {
            Enter();
            var requirement = RequireTarget(update.TargetTable);
            RequireFilter(requirement, update.Where);
            VerifyAssignments(requirement, update.Assignments);
            RequirePredicates(requirement, update.Where, SecurityScope.DmlTarget);
            var names = new[] { requirement.Table };
            foreach (var assignment in update.Assignments)
            {
                NoMaskedReads(requirement, names, assignment.Value);
                Expr(assignment.Value, ImmutableHashSet<string>.Empty, policyScope: false);
            }

            NoMaskedReads(requirement, names, update.Where);
            if (update.Where != null) Expr(update.Where, ImmutableHashSet<string>.Empty, policyScope: false);
            Exit();
        }

        public void Delete(DeleteStatement delete)
        {
            Enter();
            var requirement = RequireTarget(delete.TargetTable);
            RequireFilter(requirement, delete.Where);
            RequirePredicates(requirement, delete.Where, SecurityScope.DmlTarget);
            NoMaskedReads(requirement, new[] { requirement.Table }, delete.Where);
            if (delete.Where != null) Expr(delete.Where, ImmutableHashSet<string>.Empty, policyScope: false);
            Exit();
        }

        private void RequireFilter(TableCoverageRequirement requirement, Expression? where)
        {
            if (where is null && (Owner._dml.RejectUnfilteredDml || !requirement.RootPredicates.IsEmpty))
            {
                throw new SecurityCoverageException("An UPDATE or DELETE without a WHERE clause is not permitted.");
            }
        }

        public void Merge(MergeStatement merge)
        {
            Enter();
            if (merge.Target.Alias is null)
            {
                throw new SecurityCoverageException("A MERGE target must have an alias so that its predicates can be qualified.");
            }

            var requirement = RequireTarget(merge.Target);
            RequirePredicates(requirement, merge.On, SecurityScope.MergeOn);
            var names = new[] { merge.Target.Alias.Value, requirement.Table };
            if (!MergeAliasGuard.IsDisjoint(merge.Source, new[] { merge.Target.Alias.Value, requirement.Table, merge.Target.Name.SimpleName }))
            {
                throw new SecurityCoverageException("A MERGE source alias equals the target alias or table name.");
            }

            NoMaskedReads(requirement, names, merge.On);
            Expr(merge.On, ImmutableHashSet<string>.Empty, policyScope: false);
            Source(merge.Source, ImmutableHashSet<string>.Empty, policyScope: false);
            if (merge.Clauses.Count == 0)
            {
                throw new SecurityCoverageException("A MERGE without WHEN clauses cannot be verified.");
            }

            foreach (var clause in merge.Clauses)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                NoMaskedReads(requirement, names, clause.Condition);
                if (clause.Condition != null) Expr(clause.Condition, ImmutableHashSet<string>.Empty, policyScope: false);
                switch (clause)
                {
                    case MergeUpdateClause update:
                        VerifyAssignments(requirement, update.Assignments);
                        foreach (var assignment in update.Assignments)
                        {
                            NoMaskedReads(requirement, names, assignment.Value);
                            Expr(assignment.Value, ImmutableHashSet<string>.Empty, policyScope: false);
                        }

                        break;
                    case MergeDeleteClause:
                        break;
                    case MergeInsertClause insert:
                    {
                        int tenantIndex = VerifyInsertColumns(requirement, insert.Columns);
                        if (insert.Values.Count != insert.Columns!.Count ||
                            (tenantIndex >= 0 && !IsTenantParameter(insert.Values[tenantIndex])))
                        {
                            throw new SecurityCoverageException("A MERGE INSERT clause does not carry the bound tenant.");
                        }

                        foreach (var value in insert.Values)
                        {
                            NoMaskedReads(requirement, names, value);
                            Expr(value, ImmutableHashSet<string>.Empty, policyScope: false);
                        }

                        break;
                    }
                    default:
                        // The clause set is closed (SEC-ADG-08 a): an unknown clause such as BY SOURCE is never emitted.
                        throw new SecurityCoverageException($"MERGE clause '{clause.GetType().Name}' is not covered.");
                }
            }

            Exit();
        }
    }
}
