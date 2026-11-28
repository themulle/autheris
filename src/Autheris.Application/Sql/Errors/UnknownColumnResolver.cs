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
    /// <summary>What the client statement text says about a column reference that no catalog table, alias or CTE defines.</summary>
    internal enum Finding
    {
        /// <summary>Every reference resolves to a catalog column or to a name the statement defines (or the text could not be read).</summary>
        None,

        /// <summary>An unresolved reference exists, but its spelling is not safe to echo.</summary>
        NotEchoable,

        /// <summary>The first unresolved reference, safe to echo.</summary>
        Named
    }

    public static bool TryFind(UnknownColumnProbe probe, out string name, out int? line, out int? column) =>
        Classify(probe, out name, out line, out column) == Finding.Named;

    /// <summary>Like <see cref="TryFind"/>, but also tells an unresolved reference that must not be echoed from no unresolved reference at all.</summary>
    public static Finding Classify(UnknownColumnProbe probe, out string name, out int? line, out int? column)
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

                // The first unresolved reference is the candidate. When it is not safe to echo, the denial stays: a later reference must not
                // be reported in its place.
                if (!PublicSqlName.TryCreate(candidate, out var safe))
                {
                    return Finding.NotEchoable;
                }

                name = safe;
                if (SqlConstructLocator.TryLocateIdentifier(probe.ClientSql, safe, out int l, out int c))
                {
                    line = l;
                    column = c;
                }

                return Finding.Named;
            }
        }
        catch (Exception)
        {
            // fail closed: the caller keeps the unnamed denial
        }

        return Finding.None;
    }

    /// <summary>
    /// True only when the statement text was read and every column reference is a plain catalog column (hidden ones included) that no name
    /// of the statement itself shadows: no alias, CTE, derived or table name of that spelling, and no path of more than two parts. A refusal
    /// that remains is then about a real column's expression. Anything else, including a read failure, is false (fail closed).
    /// </summary>
    public static bool AllReferencesAreUnshadowedCatalogColumns(UnknownColumnProbe probe)
    {
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
            if (collector.DeepPath)
            {
                return false;
            }

            foreach (var candidate in collector.References)
            {
                if (!probe.CatalogColumns.Contains(candidate) || collector.Defined.Contains(candidate))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Collector : SqlAstRewriter
    {
        public List<string> References { get; } = [];

        public HashSet<string> Defined { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool DeepPath { get; private set; }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            if (node.Name.Parts.Count > 2)
            {
                DeepPath = true;
            }

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
