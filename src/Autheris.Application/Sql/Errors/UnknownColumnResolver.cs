namespace Autheris.Application.Sql.Errors;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// Finds the column of a client statement that the provider rejected as unknown. It reads only the original client text, excludes every
/// catalog column of the referenced tables (hidden ones included, so a hidden column is never reported as missing) and every name the
/// statement defines itself, and returns the first remaining name that is safe to echo. Any failure yields no name (fail closed).
/// </summary>
internal static class UnknownColumnResolver
{
    public static bool TryFind(UnknownColumnProbe probe, out string name, out int? line, out int? column)
    {
        name = string.Empty;
        line = null;
        column = null;
        try
        {
            var (tree, _) = FastSqlEngine.Default.Parse(probe.ClientSql.AsMemory(), ClientSqlTokenPolicy.Analysis);
            var statement = new SqlAstBuilder(new AstBuilderOptions
            {
                EnforceFunctionPolicy = false,
                TranslateTrinoDateFunctions = true
            }).BuildStatement(tree);

            var collector = new Collector();
            collector.Visit(statement);

            foreach (var candidate in collector.References)
            {
                if (probe.CatalogColumns.Contains(candidate) || collector.Defined.Contains(candidate))
                {
                    continue;
                }

                if (!PublicSqlName.TryCreate(candidate, out var safe))
                {
                    continue;
                }

                name = safe;
                if (SqlConstructLocator.TryLocateIdentifier(probe.ClientSql, safe, out int l, out int c))
                {
                    line = l;
                    column = c;
                }

                return true;
            }
        }
        catch (Exception)
        {
            // fail closed: the caller keeps the unnamed denial
        }

        return false;
    }

    private sealed class Collector : SqlAstRewriter
    {
        public List<string> References { get; } = [];

        public HashSet<string> Defined { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            References.Add(node.Name.SimpleName);
            return node;
        }

        public override SqlNode VisitColumnSelectItem(ColumnSelectItem node)
        {
            Add(node.Alias);
            return base.VisitColumnSelectItem(node);
        }

        public override SqlNode VisitCommonTableExpression(CommonTableExpression node)
        {
            Add(node.Name);
            AddAll(node.ColumnAliases);
            return base.VisitCommonTableExpression(node);
        }

        public override SqlNode VisitNamedTableSource(NamedTableSource node)
        {
            Defined.Add(node.Name.SimpleName);
            Add(node.Alias);
            return base.VisitNamedTableSource(node);
        }

        public override SqlNode VisitSubqueryTableSource(SubqueryTableSource node)
        {
            Add(node.Alias);
            AddAll(node.ColumnAliases);
            return base.VisitSubqueryTableSource(node);
        }

        public override SqlNode VisitLateralTableSource(LateralTableSource node)
        {
            Add(node.Alias);
            AddAll(node.ColumnAliases);
            return base.VisitLateralTableSource(node);
        }

        private void Add(SqlIdentifier? id)
        {
            if (id is not null)
            {
                Defined.Add(id.Value);
            }
        }

        private void AddAll(IReadOnlyList<SqlIdentifier>? ids)
        {
            if (ids is null)
            {
                return;
            }

            foreach (var id in ids)
            {
                Defined.Add(id.Value);
            }
        }
    }
}
