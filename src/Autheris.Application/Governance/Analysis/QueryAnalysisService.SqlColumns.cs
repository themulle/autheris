namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class QueryAnalysisService
{
    private static readonly Regex SqlJoinOnColumnRegex = new(
        @"\bJOIN\b[\s\S]*?\bON\b\s+([\s\S]+?)(?=\b(?:WHERE|GROUP\s+BY|ORDER\s+BY|LIMIT|HAVING|JOIN|INNER|LEFT|RIGHT|FULL)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlWhereClauseRegex = new(
        @"\bWHERE\b\s+([\s\S]+?)(?=\b(?:GROUP\s+BY|ORDER\s+BY|LIMIT|HAVING)\b|;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdentifierTokenRegex = new(
        @"\b([a-zA-Z_][a-zA-Z0-9_]*)(?:\.([a-zA-Z_][a-zA-Z0-9_]*))?\b",
        RegexOptions.Compiled);

    private static bool ExtractSqlProjections(
        SqlStatement? parsedStatement,
        SqlQueryMetadata metadata,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> columnsByTable,
        Dictionary<string, List<string>> extraColumnsByTable,
        HashSet<string> wildcardTables)
    {
        if (parsedStatement is not SelectStatement rootSelect)
        {
            var fallbackAll = metadata.ProjectedColumns.Count == 0 ||
                              metadata.ProjectedColumns.Any(c => c == "*" || c.EndsWith(".*", StringComparison.Ordinal));
            foreach (var rawProjected in metadata.ProjectedColumns)
            {
                var colExpr = StripSqlAlias(rawProjected);
                if (colExpr == "*" || colExpr.EndsWith(".*", StringComparison.Ordinal))
                {
                    continue;
                }

                AssignColumnReferenceToTable(colExpr, metadata.ReferencedTables, aliasToTable, visibleColumnsByTable, columnsByTable);
            }

            return fallbackAll;
        }

        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectAll = false;
        InspectSelectStatement(
            rootSelect,
            isRootStatement: true,
            cteNames,
            metadata.ReferencedTables,
            aliasToTable,
            visibleColumnsByTable,
            columnsByTable,
            extraColumnsByTable,
            wildcardTables,
            ref selectAll);
        return selectAll;
    }

    private static void InspectSelectStatement(
        SelectStatement stmt,
        bool isRootStatement,
        HashSet<string> outerCteNames,
        IReadOnlyList<TableAccessTarget> allReferencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> columnsByTable,
        Dictionary<string, List<string>> extraColumnsByTable,
        HashSet<string> wildcardTables,
        ref bool selectAll)
    {
        var scopedCtes = new HashSet<string>(outerCteNames, StringComparer.OrdinalIgnoreCase);
        if (stmt.With is { Ctes: { Count: > 0 } ctes })
        {
            foreach (var cte in ctes)
            {
                if (stmt.With.IsRecursive)
                {
                    scopedCtes.Add(cte.Name.Value);
                }

                InspectSelectStatement(
                    cte.Query,
                    isRootStatement: false,
                    scopedCtes,
                    allReferencedTables,
                    aliasToTable,
                    visibleColumnsByTable,
                    columnsByTable,
                    extraColumnsByTable,
                    wildcardTables,
                    ref selectAll);

                scopedCtes.Add(cte.Name.Value);
            }
        }

        InspectQueryBody(
            stmt.Body,
            isRootStatement,
            scopedCtes,
            allReferencedTables,
            aliasToTable,
            visibleColumnsByTable,
            columnsByTable,
            extraColumnsByTable,
            wildcardTables,
            ref selectAll);
    }

    private static void InspectQueryBody(
        QueryBody body,
        bool isRootStatement,
        HashSet<string> cteNames,
        IReadOnlyList<TableAccessTarget> allReferencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> columnsByTable,
        Dictionary<string, List<string>> extraColumnsByTable,
        HashSet<string> wildcardTables,
        ref bool selectAll)
    {
        switch (body)
        {
            case SetOperationQuery setOp:
                InspectQueryBody(setOp.Left, isRootStatement, cteNames, allReferencedTables, aliasToTable, visibleColumnsByTable, columnsByTable, extraColumnsByTable, wildcardTables, ref selectAll);
                InspectQueryBody(setOp.Right, isRootStatement, cteNames, allReferencedTables, aliasToTable, visibleColumnsByTable, columnsByTable, extraColumnsByTable, wildcardTables, ref selectAll);
                break;

            case TableQueryBody tableBody when isRootStatement:
                var fullTable = string.Join('.', tableBody.TableName.Parts.Select(p => p.Value));
                if (aliasToTable.TryGetValue(fullTable, out var resolvedKey) ||
                    aliasToTable.TryGetValue(tableBody.TableName.SimpleName, out resolvedKey))
                {
                    wildcardTables.Add(resolvedKey);
                }
                else
                {
                    selectAll = true;
                }
                break;

            case QuerySpecification qs:
                var directTableKeys = new List<string>();
                var virtualAliases = new HashSet<string>(cteNames, StringComparer.OrdinalIgnoreCase);
                var nestedSubqueries = new List<SelectStatement>();
                CollectTableSources(qs.From, cteNames, aliasToTable, directTableKeys, virtualAliases, nestedSubqueries);

                foreach (var nested in nestedSubqueries)
                {
                    InspectSelectStatement(
                        nested,
                        isRootStatement: false,
                        cteNames,
                        allReferencedTables,
                        aliasToTable,
                        visibleColumnsByTable,
                        columnsByTable,
                        extraColumnsByTable,
                        wildcardTables,
                        ref selectAll);
                }

                var directTargets = directTableKeys.Count > 0
                    ? allReferencedTables.Where(t => directTableKeys.Contains(!string.IsNullOrWhiteSpace(t.FullName) ? t.FullName : t.TableName, StringComparer.OrdinalIgnoreCase)).ToList()
                    : new List<TableAccessTarget>();

                var targetMap = isRootStatement ? columnsByTable : extraColumnsByTable;
                var projectionSubqueries = new List<SelectStatement>();
                foreach (var projection in qs.Projections)
                {
                    if (projection is WildcardSelectItem wildcard)
                    {
                        if (isRootStatement && directTableKeys.Count > 0)
                        {
                            if (wildcard.Qualifier == null)
                            {
                                foreach (var key in directTableKeys)
                                {
                                    wildcardTables.Add(key);
                                }
                            }
                            else
                            {
                                var qName = string.Join('.', wildcard.Qualifier.Parts.Select(p => p.Value));
                                if (aliasToTable.TryGetValue(qName, out var wkKey) ||
                                    aliasToTable.TryGetValue(wildcard.Qualifier.SimpleName, out wkKey))
                                {
                                    wildcardTables.Add(wkKey);
                                }
                            }
                        }

                        continue;
                    }

                    if (projection is ColumnSelectItem colItem)
                    {
                        var refs = new List<ColumnReference>();
                        CollectExpressionColumns(colItem.Expression, refs, projectionSubqueries);
                        if (directTargets.Count == 0)
                        {
                            continue;
                        }

                        foreach (var colRef in refs)
                        {
                            if (colRef.Name.Parts.Count > 1)
                            {
                                var qualifier = string.Join('.', colRef.Name.Parts.Take(colRef.Name.Parts.Count - 1).Select(p => p.Value));
                                if (virtualAliases.Contains(qualifier))
                                {
                                    continue;
                                }

                                AssignColumnReferenceToTable(
                                    $"{qualifier}.{colRef.Name.SimpleName}",
                                    directTargets,
                                    aliasToTable,
                                    visibleColumnsByTable,
                                    targetMap);
                            }
                            else
                            {
                                AssignColumnReferenceToTable(
                                    colRef.Name.SimpleName,
                                    directTargets,
                                    aliasToTable,
                                    visibleColumnsByTable,
                                    targetMap);
                            }
                        }
                    }
                }

                foreach (var nested in projectionSubqueries)
                {
                    InspectSelectStatement(
                        nested,
                        isRootStatement: false,
                        cteNames,
                        allReferencedTables,
                        aliasToTable,
                        visibleColumnsByTable,
                        columnsByTable,
                        extraColumnsByTable,
                        wildcardTables,
                        ref selectAll);
                }

                break;
        }
    }

    private static void CollectTableSources(
        TableSource? source,
        HashSet<string> cteNames,
        Dictionary<string, string> aliasToTable,
        List<string> directTableKeys,
        HashSet<string> virtualAliases,
        List<SelectStatement> nestedSubqueries)
    {
        switch (source)
        {
            case NamedTableSource named:
                var fullName = string.Join('.', named.Name.Parts.Select(p => p.Value));
                if (named.Name.Parts.Count == 1 && cteNames.Contains(fullName))
                {
                    virtualAliases.Add(fullName);
                    if (named.Alias != null)
                    {
                        virtualAliases.Add(named.Alias.Value);
                    }
                }
                else if (aliasToTable.TryGetValue(fullName, out var key) ||
                         aliasToTable.TryGetValue(named.Name.SimpleName, out key))
                {
                    if (!directTableKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        directTableKeys.Add(key);
                    }
                }
                break;

            case JoinedTableSource joined:
                CollectTableSources(joined.Left, cteNames, aliasToTable, directTableKeys, virtualAliases, nestedSubqueries);
                CollectTableSources(joined.Right, cteNames, aliasToTable, directTableKeys, virtualAliases, nestedSubqueries);
                break;

            case SubqueryTableSource sub:
                virtualAliases.Add(sub.Alias.Value);
                nestedSubqueries.Add(sub.Subquery);
                break;

            case LateralTableSource lat:
                virtualAliases.Add(lat.Alias.Value);
                nestedSubqueries.Add(lat.Subquery);
                break;
        }
    }

    private static void CollectExpressionColumns(Expression expr, List<ColumnReference> collector, List<SelectStatement> nestedSelects)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ColumnReference col)
            {
                collector.Add(col);
                continue;
            }

            if (current is SelectStatement nestedSelect)
            {
                nestedSelects.Add(nestedSelect);
                continue;
            }

            foreach (var child in current.EnumerateChildren())
            {
                stack.Push(child);
            }
        }
    }

    private static string StripSqlAlias(string projected)
    {
        var trimmed = projected.Trim();
        var asIdx = trimmed.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIdx > 0)
        {
            return trimmed[..asIdx].Trim();
        }

        return trimmed;
    }

    private static string StripIdentifierQuotes(string identifier)
    {
        var trimmed = identifier.Trim();
        return trimmed.Length >= 2 &&
               ((trimmed[0] == '"' && trimmed[^1] == '"') ||
                (trimmed[0] == '`' && trimmed[^1] == '`') ||
                (trimmed[0] == '[' && trimmed[^1] == ']'))
            ? trimmed[1..^1]
            : trimmed;
    }

    private static void AssignColumnReferenceToTable(
        string rawColumnRef,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> targetMap)
    {
        if (referencedTables.Count == 0)
        {
            return;
        }

        var trimmed = rawColumnRef.Trim();
        var dotIdx = trimmed.LastIndexOf('.', StringComparison.Ordinal);
        if (dotIdx > 0 && dotIdx < trimmed.Length - 1)
        {
            var rawPrefix = trimmed[..dotIdx];
            var normalizedPrefix = string.Join('.', rawPrefix.Split('.').Select(StripIdentifierQuotes));
            var colName = StripIdentifierQuotes(trimmed[(dotIdx + 1)..]);
            if (aliasToTable.TryGetValue(normalizedPrefix, out var resolvedTable) &&
                targetMap.TryGetValue(resolvedTable, out var list))
            {
                if (!list.Contains(colName, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(colName);
                }

                return;
            }
        }

        var cleaned = StripIdentifierQuotes(trimmed);
        if (referencedTables.Count == 1)
        {
            var onlyKey = !string.IsNullOrWhiteSpace(referencedTables[0].FullName)
                ? referencedTables[0].FullName
                : referencedTables[0].TableName;
            if (!targetMap[onlyKey].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            {
                targetMap[onlyKey].Add(cleaned);
            }

            return;
        }

        // Only consider caller-visible columns when resolving an unqualified multi-table column reference so
        // denied columns or unconsented tables behave identically to nonexistent columns.
        foreach (var target in referencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (visibleColumnsByTable.TryGetValue(key, out var visibleCols) &&
                visibleCols.Contains(cleaned))
            {
                if (!targetMap[key].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    targetMap[key].Add(cleaned);
                }

                return;
            }
        }

        var firstKey = !string.IsNullOrWhiteSpace(referencedTables[0].FullName)
            ? referencedTables[0].FullName
            : referencedTables[0].TableName;
        if (!targetMap[firstKey].Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            targetMap[firstKey].Add(cleaned);
        }
    }

    private static void ExtractAdditionalSqlFilterColumns(
        string sql,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> filterColumnsByTable)
    {
        foreach (Match match in SqlJoinOnColumnRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
        }

        foreach (Match match in SqlWhereClauseRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
        }
    }

    private static void ExtractColumnTokensFromClause(
        string clause,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, HashSet<string>> visibleColumnsByTable,
        Dictionary<string, List<string>> filterColumnsByTable)
    {
        var withoutStrings = Regex.Replace(clause, @"'[^']*'", " ");
        foreach (Match token in IdentifierTokenRegex.Matches(withoutStrings))
        {
            var full = token.Value;
            if (IsSqlKeyword(full))
            {
                continue;
            }

            if (token.Groups[2].Success)
            {
                AssignColumnReferenceToTable(full, referencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
            }
            else
            {
                var word = token.Groups[1].Value;
                if (IsSqlKeyword(word))
                {
                    continue;
                }

                foreach (var target in referencedTables)
                {
                    var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
                    if (visibleColumnsByTable.TryGetValue(key, out var visibleCols) &&
                        visibleCols.Contains(word))
                    {
                        if (!filterColumnsByTable[key].Contains(word, StringComparer.OrdinalIgnoreCase))
                        {
                            filterColumnsByTable[key].Add(word);
                        }
                    }
                }
            }
        }
    }

    private static HashSet<string> ExtractSqlEqualityFilterColumns(string sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var match = SqlWhereClauseRegex.Match(sql);
        if (!match.Success)
        {
            return result;
        }

        var clause = match.Groups[1].Value;
        var eqRegex = new Regex(@"([a-zA-Z_][a-zA-Z0-9_.]*)\s*=\s*('[^']*'|:[a-zA-Z_][a-zA-Z0-9_]*|@[a-zA-Z_][a-zA-Z0-9_]*|[0-9]+)", RegexOptions.IgnoreCase);
        foreach (Match eq in eqRegex.Matches(clause))
        {
            var rawCol = eq.Groups[1].Value;
            var col = ExtractUnqualifiedName(rawCol);
            result.Add(col);
        }

        return result;
    }

    private static bool IsSqlKeyword(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "SELECT" or "FROM" or "WHERE" or "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "OUTER" or
            "ON" or "AND" or "OR" or "NOT" or "IN" or "IS" or "NULL" or "LIKE" or "BETWEEN" or "GROUP" or
            "BY" or "ORDER" or "ASC" or "DESC" or "LIMIT" or "OFFSET" or "HAVING" or "AS" or "TRUE" or "FALSE" => true,
            _ => false
        };
    }
}
