namespace Autheris.Application.Sql.Federation.Optimization;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// PARITY-GAP-018 / SEC-PG-21..24: every column a federated statement reads, taken from the parsed AST (select items including function and
/// aggregate arguments, GROUP BY, HAVING, ORDER BY, WHERE, join conditions, nested scopes), not from the text of the select list. The
/// result only says which names a statement mentions; attribution to a table and the access level of each column stay with the planner
/// (a Deny column is never staged, a masked column is staged by the governed read, i.e. already masked).
/// </summary>
public sealed record StatementColumnReferences(
    IReadOnlyList<(string? Qualifier, string Column)> Columns,
    IReadOnlyList<string?> Wildcards,
    bool Unclassifiable)
{
    /// <summary>
    /// Collects the references. A node the walker cannot classify (unsupported AST type, execution stack limit) sets
    /// <see cref="Unclassifiable"/>: the caller then stages every allowed column of every table (SEC-PG-23), it never falls back to text.
    /// </summary>
    public static StatementColumnReferences Collect(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var collector = new Collector();
        try
        {
            collector.Visit(statement);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            // InsufficientExecutionStackException derives from InvalidOperationException.
            return new StatementColumnReferences([], [], Unclassifiable: true);
        }

        return new StatementColumnReferences(collector.Columns, collector.Wildcards, Unclassifiable: false);
    }

    private static string? QualifierOf(SqlQualifiedName name)
    {
        if (name.Parts.Count <= 1)
        {
            return null;
        }

        return string.Join('.', name.Parts.Take(name.Parts.Count - 1).Select(p => p.Value));
    }

    private sealed class Collector : SqlAstRewriter
    {
        public List<(string? Qualifier, string Column)> Columns { get; } = [];

        public List<string?> Wildcards { get; } = [];

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            Columns.Add((QualifierOf(node.Name), node.Name.SimpleName));
            return node;
        }

        public override SqlNode VisitUsingJoinCondition(UsingJoinCondition node)
        {
            foreach (var column in node.Columns)
            {
                Columns.Add((null, column.Value));
            }

            return node;
        }

        public override SqlNode VisitWildcardSelectItem(WildcardSelectItem node)
        {
            Wildcards.Add(node.Qualifier is null ? null : string.Join('.', node.Qualifier.Parts.Select(p => p.Value)));
            return node;
        }
    }
}
