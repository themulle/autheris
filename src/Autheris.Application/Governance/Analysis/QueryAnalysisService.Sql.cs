namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
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

    private async Task<SingleQueryAnalysisOutcome> AnalyzeSqlTextAsync(
        string sql,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        AnalysisScopeContext scopeContext,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        CancellationToken ct)
    {
        var outcome = new SingleQueryAnalysisOutcome();
        var trimmed = sql.Trim();

        if (!IsBalancedSqlSyntax(trimmed))
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "SQL syntax error: unbalanced parentheses, quotes, or malformed statement.",
                QueryName: queryName));
            return outcome;
        }

        SqlQueryMetadata metadata;
        SqlStatement? parsedStatement = null;
        try
        {
            var normalizedSql = SqlParameterExtractor.NormalizeForAst(trimmed);
            var (tree, _) = FastSqlEngine.Default.Parse(normalizedSql.AsMemory());
            parsedStatement = new SqlAstBuilder().BuildStatement(tree);
            var analyzer = new SqlQueryAnalyzer();
            metadata = analyzer.Analyze(tree);
        }
        catch (Exception ex)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                $"SQL syntax error: {ex.Message}",
                QueryName: queryName));
            return outcome;
        }

        if (metadata.StatementType != SqlStatementType.Select || metadata.ReferencedTables.Count == 0)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "SQL statement is not a valid SELECT query or is missing a valid FROM clause.",
                QueryName: queryName));
            return outcome;
        }

        var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            outcome.ExtractedTables.Add(key);
            aliasToTable[key] = key;
            aliasToTable[target.TableName] = key;
            aliasToTable[ExtractUnqualifiedName(key)] = key;
            if (!string.IsNullOrWhiteSpace(target.Alias))
            {
                aliasToTable[target.Alias!] = key;
            }
        }

        var selectAll = metadata.ProjectedColumns.Count == 0 ||
                        metadata.ProjectedColumns.Any(c => c == "*" || c.EndsWith(".*", StringComparison.Ordinal));

        var columnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filterColumnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            columnsByTable[key] = new List<string>();
            filterColumnsByTable[key] = new List<string>();
        }

        foreach (var rawProjected in metadata.ProjectedColumns)
        {
            var colExpr = StripSqlAlias(rawProjected);
            if (colExpr == "*" || colExpr.EndsWith(".*", StringComparison.Ordinal))
            {
                continue;
            }

            AssignColumnReferenceToTable(colExpr, metadata.ReferencedTables, aliasToTable, tableLookup, columnsByTable);
        }

        if (metadata.FilterColumnReferences != null)
        {
            foreach (var fRef in metadata.FilterColumnReferences)
            {
                var qualified = !string.IsNullOrWhiteSpace(fRef.TableOrAlias)
                    ? $"{fRef.TableOrAlias}.{fRef.ColumnName}"
                    : fRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
            }
        }

        if (metadata.JoinColumnReferences != null)
        {
            foreach (var jRef in metadata.JoinColumnReferences)
            {
                var qualified = !string.IsNullOrWhiteSpace(jRef.TableOrAlias)
                    ? $"{jRef.TableOrAlias}.{jRef.ColumnName}"
                    : jRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
            }
        }

        ExtractAdditionalSqlFilterColumns(trimmed, metadata.ReferencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        var providedFilterCols = ExtractSqlEqualityFilterColumns(trimmed);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            var tableCols = columnsByTable[key];
            var filterCols = filterColumnsByTable[key];

            await EvaluateTableAndColumnsAsync(
                key,
                tableCols,
                filterCols,
                providedFilterCols,
                queryName,
                effectivePrincipal,
                effectiveSid,
                isAdmin,
                scopeContext,
                activeConsents,
                sunsetRules,
                tableLookup,
                selectAllColumns: selectAll && tableCols.Count == 0,
                outcome,
                ct).ConfigureAwait(false);
        }

        // Issue #122: Populate Federation analysis if query routes cross-source
        var resolvedTables = new List<ResolvedSourceTable>();
        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (tableLookup.TryGetValue(key, out var catTable))
            {
                var ds = string.IsNullOrWhiteSpace(catTable.Table.SourceName)
                    ? catTable.Identifier.Domain
                    : catTable.Table.SourceName;
                resolvedTables.Add(new ResolvedSourceTable(target, catTable.Identifier, catTable, ds));
            }
        }

        if (parsedStatement != null && (resolvedTables.Count > 1 || resolvedTables.Any(t => t.Metadata.DataSourceType != DataSourceType.Sql)))
        {
            var conjuncts = SqlConjunctSplitter.Split(parsedStatement);
            var classification = SingleTableConjunctClassifier.Classify(conjuncts, resolvedTables);

            var singleConjunctsByTable = classification.SingleTableConjuncts
                .GroupBy(c => c.Table)
                .ToDictionary(g => g.Key, g => g.ToList());

            var defaultPushOptions = new PredicatePushdownOptions();

            foreach (var table in resolvedTables)
            {
                var pushedPredicates = new List<AnalyzedPushedPredicate>();
                var residualPredicates = new List<AnalyzedResidualPredicate>();

                if (table.Metadata.DataSourceType != DataSourceType.Sql)
                {
                    if (singleConjunctsByTable.TryGetValue(table.ResolvedIdentifier, out var tableConjuncts))
                    {
                        foreach (var sc in tableConjuncts)
                        {
                            foreach (var col in sc.Columns)
                            {
                                residualPredicates.Add(new AnalyzedResidualPredicate(col, RejectionReason.NotSqlSource));
                            }
                        }
                    }
                }
                else
                {
                    var columnLevels = new Dictionary<string, ColumnAccessLevel>();
                    foreach (var col in table.Metadata.Columns)
                    {
                        bool isMasked = outcome.OutputColumns.Any(o =>
                            string.Equals(o.Dataset, FormatDatasetId(table.ResolvedIdentifier), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(o.Column, col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                            o.IsMasked);

                        columnLevels[col.ColumnName] = isMasked ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear;
                    }
                    var decision = TableAccessDecision.Allowed(table.ResolvedIdentifier, columnLevels, hasUnconstrainedColumnAllow: true);

                    if (singleConjunctsByTable.TryGetValue(table.ResolvedIdentifier, out var tableConjuncts))
                    {
                        foreach (var sc in tableConjuncts)
                        {
                            var evalResult = PushabilityEvaluator.Evaluate(
                                sc,
                                table.Metadata,
                                decision,
                                table.Metadata.Dialect,
                                null,
                                defaultPushOptions);

                            if (evalResult.IsPushable)
                            {
                                var tempClause = PushedClauseBuilder.Build([sc], table.Metadata);
                                if (tempClause.PushdownPredicates != null)
                                {
                                    foreach (var p in tempClause.PushdownPredicates)
                                    {
                                        pushedPredicates.Add(new AnalyzedPushedPredicate(p.Column, p.Operator));
                                    }
                                }
                            }
                            else
                            {
                                foreach (var col in sc.Columns)
                                {
                                    residualPredicates.Add(new AnalyzedResidualPredicate(col, evalResult.RejectionReason ?? RejectionReason.UnsupportedExpression));
                                }
                            }
                        }
                    }
                }

                foreach (var mc in classification.MultiTableConjuncts)
                {
                    if (mc.Tables.Contains(table.ResolvedIdentifier))
                    {
                        var colRefs = new List<ColumnReference>();
                        SingleTableConjunctClassifier.CollectColumnReferences(mc.Predicate, colRefs);
                        foreach (var colRef in colRefs)
                        {
                            var colName = colRef.Name.Parts[^1].Value;
                            if (table.Metadata.HasColumn(colName))
                            {
                                residualPredicates.Add(new AnalyzedResidualPredicate(colName, RejectionReason.MultiTable));
                            }
                        }
                    }
                }

                var tableName = !string.IsNullOrWhiteSpace(table.Target.FullName) ? table.Target.FullName : table.Target.TableName;
                outcome.FederatedTables.Add(new AnalyzedFederatedTable(
                    Table: tableName,
                    DataSource: table.EffectiveDataSource,
                    PushdownApplied: pushedPredicates.Count > 0,
                    PushedPredicates: pushedPredicates,
                    ResidualPredicates: residualPredicates));
            }

            // Issue #124: Dynamic Key-Set Pushdown Analysis
            var keySetDecisions = new Dictionary<TableIdentifier, TableAccessDecision>();
            foreach (var table in resolvedTables)
            {
                var columnLevels = new Dictionary<string, ColumnAccessLevel>();
                foreach (var col in table.Metadata.Columns)
                {
                    bool isMasked = outcome.OutputColumns.Any(o =>
                        string.Equals(o.Dataset, FormatDatasetId(table.ResolvedIdentifier), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(o.Column, col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                        o.IsMasked);

                    columnLevels[col.ColumnName] = isMasked ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear;
                }
                keySetDecisions[table.ResolvedIdentifier] = TableAccessDecision.Allowed(table.ResolvedIdentifier, columnLevels, hasUnconstrainedColumnAllow: true);
            }

            var keySetPlans = DynamicKeySetPlanner.Plan(
                parsedStatement,
                resolvedTables,
                keySetDecisions,
                new CrossSourceOptions { DynamicKeySet = new DynamicKeySetOptions() },
                singleConjunctsByTable);

            foreach (var kp in keySetPlans)
            {
                var drivingName = !string.IsNullOrWhiteSpace(kp.DrivingTable.Target.FullName) ? kp.DrivingTable.Target.FullName : kp.DrivingTable.Target.TableName;
                var drivenName = !string.IsNullOrWhiteSpace(kp.DrivenTable.Target.FullName) ? kp.DrivenTable.Target.FullName : kp.DrivenTable.Target.TableName;
                var drivingRows = kp.DrivingTable.Metadata.EffectiveExpectedRows;
                int? estBatches = drivingRows.HasValue
                    ? Math.Max(1, (int)((drivingRows.Value + 499) / 500))
                    : null;

                outcome.KeySetPushdowns.Add(new AnalyzedKeySetPushdown(
                    DrivingTable: drivingName,
                    DrivenTable: drivenName,
                    TargetColumn: kp.DrivenKeyColumn,
                    PushedKeyCount: (int?)drivingRows,
                    EstimatedBatches: estBatches));
            }

            if (_gatewayOptions.WebSql?.CrossSource?.CoLocation?.Enabled == true)
            {
                var crossSourceOpts = _gatewayOptions.WebSql.CrossSource;
                var partitions = SubtreeGraphPartitioner.Partition(resolvedTables, _gatewayOptions.DataSources.Connections, crossSourceOpts);
                foreach (var partition in partitions)
                {
                    if (partition.IsCoLocated && partition.CoLocationGroup != null && partition.Tables.Count > 1)
                    {
                        outcome.CoLocatedGroups.Add(new CoLocatedGroupPlan(
                            GroupName: partition.CoLocationGroup,
                            Databases: partition.DistinctDatabases,
                            MemberTables: partition.Tables.Select(t => !string.IsNullOrWhiteSpace(t.Target.FullName) ? t.Target.FullName : t.Target.TableName).ToList(),
                            SingleStatementExecuted: true));
                    }
                }
            }
            // Issue #123 (WP-123-4): Subtree pushdown analysis
            if (parsedStatement != null && resolvedTables.Count >= 2)
            {
                var partitionResult = Autheris.Application.Sql.Federation.Subtrees.SubtreeGraphPartitioner.PartitionSubtrees(parsedStatement, resolvedTables);
                foreach (var st in partitionResult.Subtrees)
                {
                    var gen = TrinoSqlEngine.Ast.Generators.SqlDialectGeneratorFactory.GetGenerator(st.Dialect);
                    var genSql = gen.GenerateSql(new SelectStatement(null, new QuerySpecification(false, st.ProjectedItems, st.RootTableSource, null, null, null), null, null));
                    outcome.Subtrees.Add(new AnalyzedSubtree(
                        SubtreeId: st.SubtreeId,
                        SourceName: st.SourceName,
                        MemberTables: st.MemberTables.Select(m => m.Metadata.Identifier.ToString()).ToList(),
                        GeneratedSql: genSql,
                        ProjectedColumns: st.ProjectedItems.OfType<ColumnSelectItem>().Select(p => p.Alias?.Value ?? "col").ToList()
                    ));
                }
            }
        }

        var fieldCount = Math.Max(1, metadata.ProjectedColumns.Count);
        var joinCost = metadata.JoinCount * _analysisOptions.CostPerJoin;
        var subqueryCost = metadata.MaxSubqueryDepth * _analysisOptions.CostPerSubqueryDepth;
        var fieldCost = fieldCount * _analysisOptions.CostPerRequestedField;
        var unpaginatedPenalty = !metadata.HasExplicitLimit ? _analysisOptions.UnboundedScanPenalty : 0;

        outcome.EstimatedCost = fieldCost + joinCost + subqueryCost + unpaginatedPenalty;
        return outcome;
    }

    private static bool IsBalancedSqlSyntax(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var depth = 0;
        var inSingleQuote = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (!inSingleQuote)
            {
                if (ch == '(') depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth < 0) return false;
                }
            }
        }

        if (inSingleQuote || depth != 0)
        {
            return false;
        }

        if (sql.EndsWith("FROM", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("WHERE", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("JOIN", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith("ON", StringComparison.OrdinalIgnoreCase) ||
            sql.EndsWith(",", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
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

    private static void AssignColumnReferenceToTable(
        string rawColumnRef,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, TableMetadata> tableLookup,
        Dictionary<string, List<string>> targetMap)
    {
        var cleaned = rawColumnRef.Trim().Trim('"', '`', '[', ']');
        var dotIdx = cleaned.IndexOf('.');
        if (dotIdx > 0 && dotIdx < cleaned.Length - 1)
        {
            var prefix = cleaned[..dotIdx].Trim();
            var colName = cleaned[(dotIdx + 1)..].Trim();
            if (aliasToTable.TryGetValue(prefix, out var resolvedTable) &&
                targetMap.TryGetValue(resolvedTable, out var list))
            {
                if (!list.Contains(colName, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(colName);
                }

                return;
            }
        }

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

        foreach (var target in referencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (tableLookup.TryGetValue(key, out var catTable) &&
                catTable.Columns.Any(c => string.Equals(c.ColumnName, cleaned, StringComparison.OrdinalIgnoreCase)))
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
        Dictionary<string, TableMetadata> tableLookup,
        Dictionary<string, List<string>> filterColumnsByTable)
    {
        foreach (Match match in SqlJoinOnColumnRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        }

        foreach (Match match in SqlWhereClauseRegex.Matches(sql))
        {
            ExtractColumnTokensFromClause(match.Groups[1].Value, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
        }
    }

    private static void ExtractColumnTokensFromClause(
        string clause,
        IReadOnlyList<TableAccessTarget> referencedTables,
        Dictionary<string, string> aliasToTable,
        Dictionary<string, TableMetadata> tableLookup,
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
                AssignColumnReferenceToTable(full, referencedTables, aliasToTable, tableLookup, filterColumnsByTable);
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
                    if (tableLookup.TryGetValue(key, out var catTable) &&
                        catTable.Columns.Any(c => string.Equals(c.ColumnName, word, StringComparison.OrdinalIgnoreCase)))
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
