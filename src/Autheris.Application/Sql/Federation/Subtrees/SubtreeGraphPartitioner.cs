namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;

public sealed record SubtreePartition(
    string GroupingKey,
    bool IsCoLocated,
    string? CoLocationGroup,
    IReadOnlyList<ResolvedSourceTable> Tables)
{
    public IReadOnlyList<string> DistinctDatabases => Tables
        .Select(t => t.Metadata.Table.PhysicalDatabaseName ?? t.Metadata.Identifier.Domain)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool IsMultiDatabase => DistinctDatabases.Count > 1;
}

/// <summary>
/// Subtree graph partitioner for co-located data sources (Issue #132) and same-source AST subtrees (Issue #123).
/// Partitions query AST join trees into maximal contiguous same-source join subgraphs bounded by MaxTablesPerSubtree,
/// and partitions data source tables by co-location groups.
/// </summary>
public static class SubtreeGraphPartitioner
{
    public const int DefaultMaxTablesPerSubtree = 12;

    public static string ComputeGroupingKey(
        ResolvedSourceTable table,
        DataSourceConnectionOptions? connectionOptions,
        CrossSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        bool isCoLocationEnabled = options.CoLocation?.Enabled == true;
        if (isCoLocationEnabled &&
            connectionOptions != null &&
            !string.IsNullOrWhiteSpace(connectionOptions.CoLocationGroup) &&
            string.Equals(connectionOptions.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return $"colocated:{connectionOptions.CoLocationGroup}";
        }

        var source = table.Metadata.Table.SourceName ?? table.Metadata.Identifier.Domain;
        return $"source:{source}";
    }

    public static IReadOnlyList<SubtreePartition> Partition(
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<string, DataSourceConnectionOptions>? connectionOptionsBySource,
        CrossSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(options);

        var partitions = new List<SubtreePartition>();
        var grouped = tables.GroupBy(t =>
        {
            var source = t.Metadata.Table.SourceName ?? t.Metadata.Identifier.Domain;
            DataSourceConnectionOptions? conn = null;
            connectionOptionsBySource?.TryGetValue(source, out conn);
            return ComputeGroupingKey(t, conn, options);
        });

        foreach (var g in grouped)
        {
            bool isCoLocated = g.Key.StartsWith("colocated:", StringComparison.OrdinalIgnoreCase);
            string? groupName = isCoLocated ? g.Key["colocated:".Length..] : null;
            partitions.Add(new SubtreePartition(
                GroupingKey: g.Key,
                IsCoLocated: isCoLocated,
                CoLocationGroup: groupName,
                Tables: g.ToList()));
        }

        return partitions;
    }

    public static SubtreePartitionResult PartitionSubtrees(
        SqlStatement statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        int maxTablesPerSubtree = DefaultMaxTablesPerSubtree) =>
        Partition(statement, tables, maxTablesPerSubtree);

    public static SubtreePartitionResult Partition(
        SqlStatement statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        int maxTablesPerSubtree = DefaultMaxTablesPerSubtree)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(tables);

        if (maxTablesPerSubtree <= 0)
        {
            maxTablesPerSubtree = DefaultMaxTablesPerSubtree;
        }

        if (statement is not SelectStatement { Body: QuerySpecification querySpec } || querySpec.From == null)
        {
            return new SubtreePartitionResult(Array.Empty<SourceSubtree>(), tables);
        }

        var tableLookup = BuildTableLookup(tables);
        var finalizedClusters = new List<Cluster>();
        var clusteredTables = new HashSet<TableIdentifier>();

        var fromSource = NormalizeJoinTree(querySpec.From, tableLookup);
        var topCluster = AnalyzeTableSource(fromSource, tableLookup, maxTablesPerSubtree, finalizedClusters);
        if (topCluster != null && topCluster.IsSql && topCluster.Tables.Count >= 2)
        {
            if (topCluster.Tables.Count > maxTablesPerSubtree)
            {
                throw new GatewaySecurityException(
                    $"Subtree exceeds maximum table limit of {maxTablesPerSubtree}.",
                    "LIMIT_EXCEEDED");
            }

            finalizedClusters.Add(topCluster);
        }

        var subtrees = new List<SourceSubtree>();
        foreach (var c in finalizedClusters)
        {
            subtrees.Add(CreateSubtree(c, querySpec, ((SelectStatement)statement).OrderBy, fromSource, $"st{subtrees.Count}", tables));
        }

        foreach (var st in subtrees)
        {
            foreach (var m in st.MemberTables)
            {
                clusteredTables.Add(m.Metadata.Identifier);
            }
        }

        var remainingSingleTables = tables
            .Where(t => !clusteredTables.Contains(t.Metadata.Identifier))
            .ToList();

        return new SubtreePartitionResult(subtrees, remainingSingleTables);
    }

    private sealed class Cluster
    {
        public TableSource Root { get; set; } = null!;
        public List<ResolvedSourceTable> Tables { get; } = new();
        public string SourceName { get; set; } = string.Empty;
        public TargetSqlDialect Dialect { get; set; }
        public bool IsSql { get; set; }
    }

    private static Cluster? AnalyzeTableSource(
        TableSource source,
        Dictionary<string, ResolvedSourceTable> tableLookup,
        int maxTables,
        List<Cluster> finalizedClusters)
    {
        if (source is NamedTableSource named)
        {
            var resolved = ResolveTable(named, tableLookup);
            if (resolved == null)
            {
                return new Cluster { Root = named, IsSql = false };
            }

            bool isSql = resolved.Metadata.Table.DataSourceType == DataSourceType.Sql;
            var srcName = resolved.Metadata.Table.SourceName ?? resolved.ResolvedIdentifier.Domain;

            var c = new Cluster
            {
                Root = named,
                SourceName = srcName,
                Dialect = Autheris.Application.Sql.SqlDialectMapper.TryToTargetDialect(resolved.Metadata.Dialect, out var td) ? td : TargetSqlDialect.Ansi,
                IsSql = isSql
            };
            c.Tables.Add(resolved);
            return c;
        }

        if (source is JoinedTableSource joined)
        {
            var left = AnalyzeTableSource(joined.Left, tableLookup, maxTables, finalizedClusters);
            var right = AnalyzeTableSource(joined.Right, tableLookup, maxTables, finalizedClusters);

            bool canMerge = left != null && right != null
                && left.IsSql && right.IsSql
                && string.Equals(left.SourceName, right.SourceName, StringComparison.OrdinalIgnoreCase)
                && left.Dialect == right.Dialect
                && (left.Tables.Count + right.Tables.Count) <= maxTables
                && IsSupportedSubtreeJoin(joined.Type);

            if (canMerge)
            {
                var merged = new Cluster
                {
                    Root = joined,
                    SourceName = left!.SourceName,
                    Dialect = left.Dialect,
                    IsSql = true
                };
                merged.Tables.AddRange(left.Tables);
                merged.Tables.AddRange(right!.Tables);
                return merged;
            }

            // Cannot merge. Finalize any child that qualifies as a subtree.
            if (left != null && left.IsSql && left.Tables.Count >= 2)
            {
                if (left.Tables.Count > maxTables)
                {
                    throw new GatewaySecurityException(
                        $"Subtree exceeds maximum table limit of {maxTables}.",
                        "LIMIT_EXCEEDED");
                }
                finalizedClusters.Add(left);
            }

            if (right != null && right.IsSql && right.Tables.Count >= 2)
            {
                if (right.Tables.Count > maxTables)
                {
                    throw new GatewaySecurityException(
                        $"Subtree exceeds maximum table limit of {maxTables}.",
                        "LIMIT_EXCEEDED");
                }
                finalizedClusters.Add(right);
            }

            return new Cluster { Root = joined, IsSql = false };
        }

        return new Cluster { Root = source, IsSql = false };
    }

    private static bool IsSupportedSubtreeJoin(JoinType type) =>
        type is JoinType.Inner or JoinType.LeftOuter or JoinType.RightOuter or JoinType.FullOuter or JoinType.Cross;

    private static SourceSubtree CreateSubtree(
        Cluster cluster,
        QuerySpecification querySpec,
        OrderByClause? orderBy,
        TableSource fromSource,
        string subtreeId,
        IReadOnlyList<ResolvedSourceTable> allTables)
    {
        var memberIds = new HashSet<TableIdentifier>(cluster.Tables.Select(t => t.Metadata.Identifier));
        var memberAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in cluster.Tables)
        {
            if (!string.IsNullOrWhiteSpace(t.Target.Alias)) memberAliases.Add(t.Target.Alias);
            memberAliases.Add(t.Target.TableName);
            memberAliases.Add(t.Target.FullName);
        }

        // 1. Identify pushable predicates from WHERE
        var pushable = new List<Expression>();

        // SQLA-16: a cluster on the null-supplying side of an outer join with an outside table gets no WHERE conjuncts (they would filter before
        // the null extension); the gateway-side join evaluates them afterwards.
        if (querySpec.Where != null && !OuterJoinSides.IsNullSuppliedFromOutside(fromSource, cluster.Root))
        {
            var conjuncts = ExtractConjuncts(querySpec.Where);
            foreach (var c in conjuncts)
            {
                var colRefs = new List<ColumnReference>();
                CollectColumnReferences(c, colRefs);
                if (colRefs.Count > 0 && colRefs.All(cr => IsSubtreeColumn(cr, cluster.Tables, allTables)))
                {
                    pushable.Add(c);
                }
            }
        }

        // 2. Identify required projected items
        var projected = DetermineProjectedItems(cluster.Tables, querySpec, orderBy, pushable, allTables);

        return new SourceSubtree(
            SubtreeId: subtreeId,
            SourceName: cluster.SourceName,
            Dialect: cluster.Dialect,
            MemberTables: cluster.Tables,
            RootTableSource: cluster.Root,
            PushablePredicates: pushable,
            ProjectedItems: projected);
    }

    private static IReadOnlyList<SelectItem> DetermineProjectedItems(
        IReadOnlyList<ResolvedSourceTable> memberTables,
        QuerySpecification querySpec,
        OrderByClause? orderBy,
        IReadOnlyList<Expression> pushablePredicates,
        IReadOnlyList<ResolvedSourceTable> allTables)
    {
        var referencedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableColPairs = new List<(ResolvedSourceTable Table, string Column)>();

        void ConsiderColumn(string? qualifier, string colName)
        {
            foreach (var t in memberTables)
            {
                if (qualifier == null || MatchesTable(qualifier, t))
                {
                    if (t.Metadata.HasColumn(colName))
                    {
                        var key = $"{t.Target.TableName}.{colName}";
                        if (referencedCols.Add(key))
                        {
                            tableColPairs.Add((t, colName));
                        }
                    }
                }
            }
        }

        // (a) From outer SELECT projections
        foreach (var proj in querySpec.Projections)
        {
            if (proj is WildcardSelectItem)
            {
                foreach (var t in memberTables)
                {
                    foreach (var c in t.Metadata.Columns)
                    {
                        ConsiderColumn(t.Target.Alias ?? t.Target.TableName, c.ColumnName);
                    }
                }
            }
            else if (proj is ColumnSelectItem colSelect)
            {
                var refs = new List<ColumnReference>();
                CollectColumnReferences(colSelect.Expression, refs);
                foreach (var cr in refs)
                {
                    var colName = cr.Name.Parts[^1].Value;
                    var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;
                    ConsiderColumn(qual, colName);
                }
            }
        }

        // (b) From join conditions outside the subtree
        var allJoinRefs = new List<ColumnReference>();
        if (querySpec.From != null)
        {
            CollectJoinColumnReferencesOutside(querySpec.From, memberTables, allJoinRefs);
        }
        foreach (var cr in allJoinRefs)
        {
            var colName = cr.Name.Parts[^1].Value;
            var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;
            ConsiderColumn(qual, colName);
        }

        // (c) From residual WHERE predicates
        if (querySpec.Where != null)
        {
            var allConjuncts = ExtractConjuncts(querySpec.Where);
            foreach (var conj in allConjuncts)
            {
                if (!pushablePredicates.Contains(conj))
                {
                    var refs = new List<ColumnReference>();
                    CollectColumnReferences(conj, refs);
                    foreach (var cr in refs)
                    {
                        var colName = cr.Name.Parts[^1].Value;
                        var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;
                        ConsiderColumn(qual, colName);
                    }
                }
            }
        }

        // (d) GroupBy / Having / OrderBy
        if (querySpec.GroupBy != null)
        {
            foreach (var expr in querySpec.GroupBy.GroupingExpressions)
            {
                var refs = new List<ColumnReference>();
                CollectColumnReferences(expr, refs);
                foreach (var cr in refs)
                {
                    ConsiderColumn(cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null, cr.Name.Parts[^1].Value);
                }
            }
        }
        if (querySpec.Having != null)
        {
            var refs = new List<ColumnReference>();
            CollectColumnReferences(querySpec.Having, refs);
            foreach (var cr in refs)
            {
                ConsiderColumn(cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null, cr.Name.Parts[^1].Value);
            }
        }

        // SQLA-17: the ORDER BY of the statement is evaluated by DuckDB on the staged table, so its subtree columns must be staged too.
        if (orderBy != null)
        {
            foreach (var element in orderBy.Elements)
            {
                var refs = new List<ColumnReference>();
                CollectColumnReferences(element.Expression, refs);
                foreach (var cr in refs)
                {
                    ConsiderColumn(cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null, cr.Name.Parts[^1].Value);
                }
            }
        }

        // If no column referenced, project first column of first table
        if (tableColPairs.Count == 0 && memberTables.Count > 0 && memberTables[0].Metadata.Columns.Count > 0)
        {
            tableColPairs.Add((memberTables[0], memberTables[0].Metadata.Columns[0].ColumnName));
        }

        // Build SelectItem list with alias disambiguation
        var colCounts = tableColPairs.GroupBy(p => p.Column, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var selectItems = new List<SelectItem>(tableColPairs.Count);
        foreach (var (t, col) in tableColPairs)
        {
            string alias = colCounts[col] > 1
                ? $"{(t.Target.Alias ?? t.Target.TableName)}_{col}"
                : col;

            var tableQual = t.Target.Alias ?? t.Target.TableName;
            var colRef = new ColumnReference(new SqlQualifiedName([
                new SqlIdentifier(tableQual, false),
                new SqlIdentifier(col, false)
            ]));

            selectItems.Add(new ColumnSelectItem(colRef, new SqlIdentifier(alias, false)));
        }

        return selectItems;
    }

    private static void CollectJoinColumnReferencesOutside(
        TableSource source,
        IReadOnlyList<ResolvedSourceTable> memberTables,
        List<ColumnReference> results)
    {
        if (source is JoinedTableSource joined)
        {
            // If this joined node connects tables across the subtree boundary, collect its condition columns
            bool leftInSubtree = ContainsAny(joined.Left, memberTables);
            bool rightInSubtree = ContainsAny(joined.Right, memberTables);

            if (leftInSubtree != rightInSubtree || (!leftInSubtree && !rightInSubtree))
            {
                if (joined.Condition is OnJoinCondition onCond)
                {
                    CollectColumnReferences(onCond.Predicate, results);
                }
            }

            CollectJoinColumnReferencesOutside(joined.Left, memberTables, results);
            CollectJoinColumnReferencesOutside(joined.Right, memberTables, results);
        }
    }

    private static bool ContainsAny(TableSource source, IReadOnlyList<ResolvedSourceTable> tables)
    {
        if (source is NamedTableSource named)
        {
            return tables.Any(t => MatchesTable(named, t));
        }
        if (source is JoinedTableSource joined)
        {
            return ContainsAny(joined.Left, tables) || ContainsAny(joined.Right, tables);
        }
        return false;
    }

    private static bool MatchesTable(NamedTableSource named, ResolvedSourceTable table)
    {
        var rawName = string.Join(".", named.Name.Parts.Select(p => p.Value));
        return MatchesTable(rawName, table) || (named.Alias != null && string.Equals(named.Alias.Value, table.Target.Alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesTable(string qualifier, ResolvedSourceTable table)
    {
        return string.Equals(qualifier, table.Target.Alias, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Target.FullName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Metadata.Identifier.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubtreeColumn(
        ColumnReference cr,
        IReadOnlyList<ResolvedSourceTable> subtreeMembers,
        IReadOnlyList<ResolvedSourceTable> allTables)
    {
        var colName = cr.Name.Parts[^1].Value;
        var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;

        if (qual != null)
        {
            return subtreeMembers.Any(m => MatchesTable(qual, m));
        }

        // Unqualified: check if only subtree member has it
        bool inSubtree = subtreeMembers.Any(m => m.Metadata.HasColumn(colName));
        bool outsideSubtree = allTables.Except(subtreeMembers).Any(o => o.Metadata.HasColumn(colName));
        return inSubtree && !outsideSubtree;
    }

    private static List<Expression> ExtractConjuncts(Expression where)
    {
        var list = new List<Expression>();
        void Flatten(Expression e)
        {
            if (e is BinaryExpression { Operator: BinaryOperator.And } b)
            {
                Flatten(b.Left);
                Flatten(b.Right);
            }
            else
            {
                list.Add(e);
            }
        }
        Flatten(where);
        return list;
    }

    private static void CollectColumnReferences(SqlNode node, List<ColumnReference> list)
    {
        if (node is ColumnReference cr)
        {
            list.Add(cr);
            return;
        }

        // Recursively inspect children
        foreach (var prop in node.GetType().GetProperties())
        {
            if (typeof(SqlNode).IsAssignableFrom(prop.PropertyType))
            {
                if (prop.GetValue(node) is SqlNode child)
                {
                    CollectColumnReferences(child, list);
                }
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(string))
            {
                if (prop.GetValue(node) is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is SqlNode childNode)
                        {
                            CollectColumnReferences(childNode, list);
                        }
                    }
                }
            }
        }
    }

    private static Dictionary<string, ResolvedSourceTable> BuildTableLookup(IReadOnlyList<ResolvedSourceTable> tables)
    {
        var lookup = new Dictionary<string, ResolvedSourceTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            lookup[t.Target.FullName] = t;
            lookup[t.Target.TableName] = t;
            lookup[t.Metadata.Identifier.ToString()] = t;
            if (!string.IsNullOrWhiteSpace(t.Target.Alias))
            {
                lookup[t.Target.Alias] = t;
            }
        }
        return lookup;
    }

    private static ResolvedSourceTable? ResolveTable(NamedTableSource named, Dictionary<string, ResolvedSourceTable> lookup)
    {
        var fullName = string.Join(".", named.Name.Parts.Select(p => p.Value));
        if (lookup.TryGetValue(fullName, out var r)) return r;
        var lastName = named.Name.Parts[^1].Value;
        if (lookup.TryGetValue(lastName, out var rLast)) return rLast;
        if (named.Alias != null && lookup.TryGetValue(named.Alias.Value, out var rAlias)) return rAlias;
        return null;
    }

    private static TableSource NormalizeJoinTree(TableSource source, Dictionary<string, ResolvedSourceTable> lookup)
    {
        if (source is not JoinedTableSource joined)
        {
            return source;
        }

        var normalizedLeft = NormalizeJoinTree(joined.Left, lookup);
        var normalizedRight = NormalizeJoinTree(joined.Right, lookup);

        if (normalizedLeft is JoinedTableSource leftJoined
            && leftJoined.Type is JoinType.Inner or JoinType.Cross
            && joined.Type is JoinType.Inner or JoinType.Cross)
        {
            var r1 = GetSingleTableSource(leftJoined.Right, lookup);
            var r2 = GetSingleTableSource(normalizedRight, lookup);

            if (r1 != null && r2 != null
                && r1.Metadata.Table.DataSourceType == DataSourceType.Sql
                && r2.Metadata.Table.DataSourceType == DataSourceType.Sql
                && string.Equals(r1.Metadata.Table.SourceName ?? r1.ResolvedIdentifier.Domain,
                                 r2.Metadata.Table.SourceName ?? r2.ResolvedIdentifier.Domain,
                                 StringComparison.OrdinalIgnoreCase))
            {
                var lTable = GetSingleTableSource(leftJoined.Left, lookup);
                if (lTable == null || !string.Equals(lTable.Metadata.Table.SourceName ?? lTable.ResolvedIdentifier.Domain,
                                                    r1.Metadata.Table.SourceName ?? r1.ResolvedIdentifier.Domain,
                                                    StringComparison.OrdinalIgnoreCase))
                {
                    // Rotate: (L JOIN R1) JOIN R2 -> L JOIN (R1 JOIN R2)
                    var newRight = new JoinedTableSource(leftJoined.Right, joined.Type, normalizedRight, joined.Condition);
                    return new JoinedTableSource(leftJoined.Left, leftJoined.Type, newRight, leftJoined.Condition);
                }
            }
        }

        return new JoinedTableSource(normalizedLeft, joined.Type, normalizedRight, joined.Condition);
    }

    private static ResolvedSourceTable? GetSingleTableSource(TableSource source, Dictionary<string, ResolvedSourceTable> lookup)
    {
        if (source is NamedTableSource named)
        {
            return ResolveTable(named, lookup);
        }
        if (source is JoinedTableSource joined)
        {
            return GetSingleTableSource(joined.Right, lookup) ?? GetSingleTableSource(joined.Left, lookup);
        }
        return null;
    }
}
