using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Ast.Visitors;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    public override SqlNode VisitTableQueryBody(TableQueryBody node)
    {
        var namedTableSource = new NamedTableSource(node.TableName, null);
        var securedSource = (TableSource)Visit(namedTableSource);
        return new QuerySpecification(
            Distinct: false,
            Projections: new[] { new WildcardSelectItem(null) },
            From: securedSource,
            Where: null,
            GroupBy: null,
            Having: null);
    }

    public override SqlNode VisitDeleteStatement(DeleteStatement node)
    {
        if (_typed != null) return SecureDeleteTyped(node);

        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;

        string? targetAlias = node.TargetTable.Alias?.Value;

        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in WHERE
        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, node.Where, "DELETE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "DELETE");

        // Visit WHERE clause so subqueries within WHERE have RLS/masking applied
        var visitedWhere = node.Where != null ? (Expression)Visit(node.Where) : null;

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node with { Where = visitedWhere };
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = PolicyFilterExpression(policyFilter);
        Expression combinedWhere = visitedWhere != null
            ? (Expression)new BinaryExpression(new ParenthesizedExpression(visitedWhere), BinaryOperator.And, new ParenthesizedExpression(rlsFilter))
            : new ParenthesizedExpression(rlsFilter);

        return node with { Where = combinedWhere };
    }

    public override SqlNode VisitUpdateStatement(UpdateStatement node)
    {
        if (_typed != null) return SecureUpdateTyped(node);

        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;
        string? targetAlias = node.TargetTable.Alias?.Value;

        var visitedAssignments = new List<UpdateAssignment>(node.Assignments.Count);
        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in SET or WHERE
        foreach (var assignment in node.Assignments)
        {
            string colName = assignment.Column.Value;
            if (_options.ColumnMaskingProvider != null && _options.ColumnMaskingProvider.HasMask(normalizedName, colName))
            {
                throw new SecurityException($"Masked column '{colName}' of table '{normalizedName}' must not be referenced in UPDATE SET.");
            }
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, assignment.Value, "UPDATE SET");

            var visitedValue = (Expression)Visit(assignment.Value);
            visitedAssignments.Add(assignment with { Value = visitedValue });
        }

        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, node.Where, "UPDATE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "UPDATE");

        // Visit WHERE clause so subqueries within WHERE have RLS/masking applied
        var visitedWhere = node.Where != null ? (Expression)Visit(node.Where) : null;

        // WITH CHECK OPTION verification on assignments
        string tenantColumn = _options.GetTenantColumnName(normalizedName);
        if (_options.EnforceWithCheckOption)
        {
            foreach (var assignment in visitedAssignments)
            {
                string colName = assignment.Column.Value;
                if (colName.Equals(tenantColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (_options.DisallowTenantColumnModificationInUpdate)
                    {
                        throw new SecurityException($"Modification of tenant column '{colName}' is not allowed in UPDATE statement.");
                    }

                    if (string.IsNullOrEmpty(_options.ExpectedTenantValue))
                    {
                        throw new SecurityException("Expected tenant value must be configured when WITH CHECK OPTION is active.");
                    }

                    string expectedTenant = SqlIdentifierHelper.UnquoteStringLiteral(_options.ExpectedTenantValue);
                    EnsureTenantLiteral(assignment.Value, expectedTenant, "UPDATE", tenantColumn);
                }
            }
        }

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node with { Assignments = visitedAssignments.AsReadOnly(), Where = visitedWhere };
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = PolicyFilterExpression(policyFilter);
        Expression combinedWhere = visitedWhere != null
            ? (Expression)new BinaryExpression(new ParenthesizedExpression(visitedWhere), BinaryOperator.And, new ParenthesizedExpression(rlsFilter))
            : new ParenthesizedExpression(rlsFilter);

        return node with { Assignments = visitedAssignments.AsReadOnly(), Where = combinedWhere };
    }

    public override SqlNode VisitInsertStatement(InsertStatement node)
    {
        if (_typed != null) return SecureInsertTyped(node);

        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;
        string simpleTableName = node.TargetTable.Name.SimpleName;
        string tenantColumn = _options.GetTenantColumnName(normalizedName);

        // SQ-07: Reject INSERT on tables that have custom row-level consent filters beyond simple tenant partition
        if (_options.RejectConsentFilteredInsert && _options.TablesWithConsentRowFilter.Count > 0)
        {
            if (_options.TablesWithConsentRowFilter.Contains(normalizedName) ||
                _options.TablesWithConsentRowFilter.Contains(simpleTableName))
            {
                throw new SecurityException($"INSERT into table '{normalizedName}' with custom row-level consent filter is not permitted.");
            }
        }

        if (_options.EnforceWithCheckOption)
        {
            int tenantIndex = -1;
            if (node.Columns != null)
            {
                for (int i = 0; i < node.Columns.Count; i++)
                {
                    if (node.Columns[i].Value.Equals(tenantColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        tenantIndex = i;
                        break;
                    }
                }
            }

            if (tenantIndex == -1)
            {
                if (_options.RequireTenantColumnInInsert)
                {
                    throw new SecurityException($"Tenant column '{tenantColumn}' must be explicitly specified in INSERT statement.");
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_options.ExpectedTenantValue))
                {
                    throw new SecurityException("Expected tenant value must be configured when WITH CHECK OPTION is active.");
                }
                string expectedTenant = SqlIdentifierHelper.UnquoteStringLiteral(_options.ExpectedTenantValue);
                VerifyInsertSource(node.Source, tenantIndex, expectedTenant, tenantColumn);
            }
        }

        var source = (QueryBody)Visit(node.Source);
        if (source == node.Source) return node;
        return node with { Source = source };
    }

    private void VerifyInsertSource(QueryBody source, int tenantIndex, string expectedTenant, string tenantColumn)
    {
        switch (source)
        {
            case ValuesQueryBody values:
                if (values.Rows.Count == 0)
                {
                    throw new SecurityException("INSERT VALUES clause without rows cannot be verified.");
                }
                foreach (var row in values.Rows)
                {
                    if (tenantIndex >= row.Elements.Count)
                    {
                        throw new SecurityException($"Tenant column '{tenantColumn}' has no value in an INSERT VALUES row.");
                    }
                    EnsureTenantLiteral(row.Elements[tenantIndex], expectedTenant, "INSERT", tenantColumn);
                }
                break;
            case QuerySpecification select:
                for (int i = 0; i < select.Projections.Count && i <= tenantIndex; i++)
                {
                    if (select.Projections[i] is not ColumnSelectItem)
                    {
                        throw new SecurityException($"Tenant column '{tenantColumn}' in INSERT SELECT cannot be verified (wildcard projection).");
                    }
                }
                if (tenantIndex >= select.Projections.Count || select.Projections[tenantIndex] is not ColumnSelectItem colItem)
                {
                    throw new SecurityException($"Tenant column '{tenantColumn}' has no value in INSERT SELECT.");
                }
                EnsureTenantLiteral(colItem.Expression, expectedTenant, "INSERT SELECT", tenantColumn);
                break;
            case SetOperationQuery setOp:
                VerifyInsertSource(setOp.Left, tenantIndex, expectedTenant, tenantColumn);
                VerifyInsertSource(setOp.Right, tenantIndex, expectedTenant, tenantColumn);
                break;
            default:
                throw new SecurityException("INSERT source query shape cannot be verified against the tenant WITH CHECK OPTION.");
        }
    }

    private void EnsureTenantLiteral(Expression expr, string expectedTenant, string operation, string tenantColumn)
    {
        if (expr is not LiteralExpression lit || lit.Value == null)
        {
            throw new SecurityException($"Tenant column '{tenantColumn}' in {operation} must be a literal value.");
        }

        string val = lit.Value.ToString() ?? string.Empty;
        if (!val.Equals(expectedTenant, StringComparison.Ordinal))
        {
            throw new SecurityException($"Tenant column '{tenantColumn}' {operation} value '{val}' does not match expected tenant '{expectedTenant}'.");
        }
    }

    private void EnsureFilteredDml(Expression? where, string operation)
    {
        if (!_options.RejectUnfilteredDml) return;

        if (where == null)
        {
            throw new UnfilteredDmlException($"{operation} without a WHERE clause is not permitted.");
        }

        // Plan 1: DML statement WHERE clause must reference at least one table column; literal-only predicates are forbidden
        if (!ContainsColumnReference(where))
        {
            throw new UnfilteredDmlException($"{operation} statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.");
        }

        if (IsTriviallyTrue(where))
        {
            throw new UnfilteredDmlException($"{operation} with a trivially true WHERE clause is not permitted.");
        }
    }

    private static bool ContainsColumnReference(Expression? expr)
    {
        if (expr == null) return false;

        return expr switch
        {
            ColumnReference => true,
            BinaryExpression b => ContainsColumnReference(b.Left) || ContainsColumnReference(b.Right),
            UnaryExpression u => ContainsColumnReference(u.Operand),
            LikeExpression l => ContainsColumnReference(l.Operand) || ContainsColumnReference(l.Pattern) || (l.Escape != null && ContainsColumnReference(l.Escape)),
            InListExpression inList => ContainsColumnReference(inList.Operand) || inList.Items.Any(ContainsColumnReference),
            InSubqueryExpression inSq => ContainsColumnReference(inSq.Operand) || ContainsColumnReferenceInQuery(inSq.Subquery),
            ExistsExpression ex => ContainsColumnReferenceInQuery(ex.Subquery),
            ScalarSubqueryExpression sc => ContainsColumnReferenceInQuery(sc.Subquery),
            BetweenExpression between => ContainsColumnReference(between.Operand) || ContainsColumnReference(between.Lower) || ContainsColumnReference(between.Upper),
            CaseExpression caseExpr => (caseExpr.Operand != null && ContainsColumnReference(caseExpr.Operand)) ||
                                       caseExpr.WhenClauses.Any(w => ContainsColumnReference(w.Condition) || ContainsColumnReference(w.Result)) ||
                                       (caseExpr.ElseResult != null && ContainsColumnReference(caseExpr.ElseResult)),
            FunctionCallExpression func => func.Arguments.Any(ContainsColumnReference),
            SubstringExpression sub => ContainsColumnReference(sub.Source) || ContainsColumnReference(sub.Start) || (sub.Length != null && ContainsColumnReference(sub.Length)),
            TrimExpression trim => ContainsColumnReference(trim.Source) || (trim.Characters != null && ContainsColumnReference(trim.Characters)),
            PositionExpression pos => ContainsColumnReference(pos.Needle) || ContainsColumnReference(pos.Haystack),
            CastExpression cast => ContainsColumnReference(cast.Operand),
            DateFunctionExpression dateFunc => ContainsColumnReference(dateFunc.Source),
            ExtractExpression extract => ContainsColumnReference(extract.Source),
            IsDistinctFromExpression distinct => ContainsColumnReference(distinct.Left) || ContainsColumnReference(distinct.Right),
            QuantifiedComparisonExpression quant => ContainsColumnReference(quant.Left) || ContainsColumnReferenceInQuery(quant.Subquery),
            _ => false
        };
    }

    private static bool ContainsColumnReferenceInQuery(SelectStatement subquery)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(subquery);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference or WildcardSelectItem or UsingJoinCondition)
            {
                return true;
            }
            PushChildren(node, stack);
        }

        return false;
    }

    private static bool TryCompareNumericLiterals(LiteralExpression left, LiteralExpression right, out int comparison)
    {
        comparison = 0;
        if (left.Value == null || right.Value == null) return false;

        if (double.TryParse(left.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d1) &&
            double.TryParse(right.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d2))
        {
            comparison = d1.CompareTo(d2);
            return true;
        }

        return false;
    }

    private static bool IsTriviallyTrue(Expression? expr)
    {
        if (expr == null) return false;

        switch (expr)
        {
            case ParenthesizedExpression p:
                return IsTriviallyTrue(p.Expression);
            case LiteralExpression lit when lit.Type == LiteralType.Boolean:
                return true.Equals(lit.Value);
            case BinaryExpression b when b.Operator == BinaryOperator.Or:
                return IsTriviallyTrue(b.Left) || IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator == BinaryOperator.And:
                return IsTriviallyTrue(b.Left) && IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator == BinaryOperator.Equal:
                if (b.Left is LiteralExpression el1 && b.Right is LiteralExpression el2)
                {
                    if (TryCompareNumericLiterals(el1, el2, out int cmpEq))
                        return cmpEq == 0;
                    return Equals(el1.Value?.ToString(), el2.Value?.ToString());
                }
                if (b.Left is ColumnReference ec1 && b.Right is ColumnReference ec2)
                {
                    return ec1.Name.NormalizedName.Equals(ec2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.NotEqual:
                if (b.Left is LiteralExpression nl1 && b.Right is LiteralExpression nl2)
                {
                    if (TryCompareNumericLiterals(nl1, nl2, out int cmpNeq))
                        return cmpNeq != 0;
                    return !Equals(nl1.Value?.ToString(), nl2.Value?.ToString());
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThan:
                if (b.Left is LiteralExpression ltl && b.Right is LiteralExpression ltr &&
                    TryCompareNumericLiterals(ltl, ltr, out int cmpLt))
                {
                    return cmpLt < 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThanOrEqual:
                if (b.Left is LiteralExpression le1 && b.Right is LiteralExpression le2)
                {
                    if (TryCompareNumericLiterals(le1, le2, out int cmpLe))
                        return cmpLe <= 0;
                    return Equals(le1.Value?.ToString(), le2.Value?.ToString());
                }
                if (b.Left is ColumnReference leCol1 && b.Right is ColumnReference leCol2)
                {
                    return leCol1.Name.NormalizedName.Equals(leCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThan:
                if (b.Left is LiteralExpression gtl && b.Right is LiteralExpression gtr &&
                    TryCompareNumericLiterals(gtl, gtr, out int cmpGt))
                {
                    return cmpGt > 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThanOrEqual:
                if (b.Left is LiteralExpression ge1 && b.Right is LiteralExpression ge2)
                {
                    if (TryCompareNumericLiterals(ge1, ge2, out int cmpGe))
                        return cmpGe >= 0;
                    return Equals(ge1.Value?.ToString(), ge2.Value?.ToString());
                }
                if (b.Left is ColumnReference geCol1 && b.Right is ColumnReference geCol2)
                {
                    return geCol1.Name.NormalizedName.Equals(geCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BetweenExpression between:
                if (between.Operand is ColumnReference opCol &&
                    between.Lower is ColumnReference lowCol &&
                    between.Upper is ColumnReference upCol)
                {
                    if (opCol.Name.NormalizedName.Equals(lowCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase) &&
                        opCol.Name.NormalizedName.Equals(upCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            case InListExpression inList:
                if (inList.Operand is ColumnReference inCol &&
                    inList.Items.Any(item => item is ColumnReference itemCol &&
                                            itemCol.Name.NormalizedName.Equals(inCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                return false;
            case UnaryExpression u when u.Operator == UnaryOperator.Not:
                return u.Operand is LiteralExpression boolLit && boolLit.Type == LiteralType.Boolean && false.Equals(boolLit.Value);
            case UnaryExpression u when u.Operator == UnaryOperator.IsNotNull:
                return u.Operand is LiteralExpression constLit && constLit.Type != LiteralType.Null;
            default:
                return false;
        }
    }
}
