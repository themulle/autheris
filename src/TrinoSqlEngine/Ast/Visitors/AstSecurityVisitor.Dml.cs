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

    public override SqlNode VisitDeleteStatement(DeleteStatement node) => SecureDeleteTyped(node);

    public override SqlNode VisitUpdateStatement(UpdateStatement node) => SecureUpdateTyped(node);

    public override SqlNode VisitInsertStatement(InsertStatement node) => SecureInsertTyped(node);

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
}

