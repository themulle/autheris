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
        var spec = new QuerySpecification(
            Distinct: false,
            Projections: new[] { new WildcardSelectItem(null) },
            From: new NamedTableSource(node.TableName, null),
            Where: null,
            GroupBy: null,
            Having: null);
        return Visit(spec);
    }

    public override SqlNode VisitDeleteStatement(DeleteStatement node) => SecureDeleteTyped(node);

    public override SqlNode VisitUpdateStatement(UpdateStatement node) => SecureUpdateTyped(node);

    public override SqlNode VisitInsertStatement(InsertStatement node) => SecureInsertTyped(node);

    /// <summary>
    /// A client parameter tested with <c>IS NULL</c> inside an UPDATE/DELETE WHERE clause is the optional filter shape
    /// <c>(@p IS NULL OR X)</c>: a NULL value collapses the predicate to always true and widens the statement to every row up to
    /// the row cap. The refusal never depends on parameter values, so it applies to every client parameter, bounded or not.
    /// </summary>
    private static void RejectOptionalClientFilter(Expression? where, string operation)
    {
        if (where is null) return;

        bool found = false;
        AstReflection.Walk(where, node =>
        {
            if (node is UnaryExpression
                {
                    Operator: UnaryOperator.IsNull,
                    Operand: ParameterReference { IsSynthetic: true, PositionalIndex: null } reference
                } && reference.ListCount is null)
            {
                found = true;
                return false;
            }

            return !found;
        });

        if (found)
        {
            throw new UnfilteredDmlException($"{operation} does not permit an optional filter on a client parameter (IS NULL); a NULL value would match every row.");
        }
    }

    private void EnsureFilteredDml(Expression? where, string operation)
    {
        RejectOptionalClientFilter(where, operation);
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
}

