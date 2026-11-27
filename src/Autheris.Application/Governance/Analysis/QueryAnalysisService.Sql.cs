namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class QueryAnalysisService
{
    private async Task<SingleQueryAnalysisOutcome> AnalyzeSqlTextCoreAsync(
        string sql,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        AnalysisScopeContext scopeContext,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        bool adHocStatement,
        CancellationToken ct)
    {
        var outcome = new SingleQueryAnalysisOutcome();
        var trimmed = sql.Trim();

        if (!IsBalancedSqlSyntax(trimmed))
        {
            outcome.Diagnostics.Add(adHocStatement
                ? AdHocSyntaxDiagnostic(queryName)
                : new QueryValidationDiagnostic(
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
            var (tree, _) = await FastSqlEngine.Default.ParseAsync(normalizedSql.AsMemory(), null, ct).ConfigureAwait(false);
            parsedStatement = new SqlAstBuilder(new AstBuilderOptions
            {
                EnforceFunctionPolicy = true,
                AllowedFunctions = SqlDialectMapper.AllSafeDialectFunctions
            }).BuildStatement(tree);
            var analyzer = new SqlQueryAnalyzer();
            metadata = analyzer.Analyze(tree);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // An ad-hoc statement never echoes the parser's text: the fixed message of the code only.
            outcome.Diagnostics.Add(adHocStatement
                ? AdHocSyntaxDiagnostic(queryName)
                : new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.SyntaxError,
                    $"SQL syntax error: {ex.Message}",
                    QueryName: queryName));
            return outcome;
        }

        if (metadata.StatementType != SqlStatementType.Select || metadata.ReferencedTables.Count == 0)
        {
            outcome.Diagnostics.Add(adHocStatement
                ? AdHocSyntaxDiagnostic(queryName)
                : new QueryValidationDiagnostic(
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
            if (!string.IsNullOrWhiteSpace(target.Schema))
            {
                aliasToTable[$"{target.Schema}.{target.TableName}"] = key;
            }
            if (!string.IsNullOrWhiteSpace(target.Catalog) && !string.IsNullOrWhiteSpace(target.Schema))
            {
                aliasToTable[$"{target.Catalog}.{target.Schema}.{target.TableName}"] = key;
            }
            if (!string.IsNullOrWhiteSpace(target.Alias))
            {
                aliasToTable[target.Alias!] = key;
            }
        }

        var visibleColumnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (!visibleColumnsByTable.ContainsKey(key) && tableLookup.TryGetValue(key, out var catTable))
            {
                var visibleCols = await ResolveVisibleColumnSetAsync(
                    catTable,
                    effectivePrincipal,
                    effectiveSid,
                    isAdmin,
                    scopeContext,
                    activeConsents,
                    ct).ConfigureAwait(false);
                if (visibleCols != null)
                {
                    visibleColumnsByTable[key] = visibleCols;
                }
            }
        }

        var columnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var extraColumnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filterColumnsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var wildcardTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            columnsByTable[key] = new List<string>();
            extraColumnsByTable[key] = new List<string>();
            filterColumnsByTable[key] = new List<string>();
        }

        var selectAll = ExtractSqlProjections(
            parsedStatement,
            metadata,
            aliasToTable,
            visibleColumnsByTable,
            columnsByTable,
            extraColumnsByTable,
            wildcardTables);

        var filterAndWindowRefs = (metadata.FilterColumnReferences ?? [])
            .Concat(metadata.WindowColumnReferences ?? [])
            .Concat(metadata.GroupByColumnReferences ?? []);
        {
            foreach (var fRef in filterAndWindowRefs)
            {
                var qualified = !string.IsNullOrWhiteSpace(fRef.TableOrAlias)
                    ? $"{fRef.TableOrAlias}.{fRef.ColumnName}"
                    : fRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
            }
        }

        if (metadata.JoinColumnReferences != null)
        {
            foreach (var jRef in metadata.JoinColumnReferences)
            {
                var qualified = !string.IsNullOrWhiteSpace(jRef.TableOrAlias)
                    ? $"{jRef.TableOrAlias}.{jRef.ColumnName}"
                    : jRef.ColumnName;
                AssignColumnReferenceToTable(qualified, metadata.ReferencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
            }
        }

        ExtractAdditionalSqlFilterColumns(trimmed, metadata.ReferencedTables, aliasToTable, visibleColumnsByTable, filterColumnsByTable);
        var providedFilterCols = ExtractSqlEqualityFilterColumns(trimmed);

        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            var tableCols = columnsByTable[key];
            var extraCols = extraColumnsByTable[key];
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
                selectAllColumns: wildcardTables.Contains(key) || (selectAll && tableCols.Count == 0),
                outcome,
                ct,
                extraValidationColumns: extraCols).ConfigureAwait(false);
        }

        // Issue #122: Populate Federation analysis if query routes cross-source
        var resolvedTables = new List<ResolvedSourceTable>();
        foreach (var target in metadata.ReferencedTables)
        {
            var key = !string.IsNullOrWhiteSpace(target.FullName) ? target.FullName : target.TableName;
            if (tableLookup.TryGetValue(key, out var catTable))
            {
                if (catTable.Table.DiscoveryHidden && !scopeContext.TableDecisions.ContainsKey(catTable.Identifier))
                {
                    continue;
                }

                var ds = string.IsNullOrWhiteSpace(catTable.Table.SourceName)
                    ? catTable.Identifier.Domain
                    : catTable.Table.SourceName;
                resolvedTables.Add(new ResolvedSourceTable(target, catTable.Identifier, catTable, ds));
            }
        }

        if (parsedStatement != null && (resolvedTables.Count > 1 || resolvedTables.Any(t => t.Metadata.DataSourceType != DataSourceType.Sql)))
        {
            var visibleResolvedTables = new List<ResolvedSourceTable>(resolvedTables.Count);
            var visibleMetadataByTable = new Dictionary<TableIdentifier, TableMetadata>();
            foreach (var table in resolvedTables)
            {
                var decision = scopeContext.TableDecisions.TryGetValue(table.ResolvedIdentifier, out var dec) ? dec : null;
                var visibleCols = decision is { IsAllowed: true }
                    ? table.Metadata.Columns
                        .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, table.Metadata) != ColumnAccessLevel.Deny)
                        .ToList()
                    : Array.Empty<TableColumn>().ToList();
                var visibleMeta = table.Metadata with { Columns = visibleCols };
                visibleMetadataByTable[table.ResolvedIdentifier] = visibleMeta;
                visibleResolvedTables.Add(table with { Metadata = visibleMeta });
            }

            var conjuncts = SqlConjunctSplitter.Split(parsedStatement);
            var classification = SingleTableConjunctClassifier.Classify(conjuncts, visibleResolvedTables);

            var singleConjunctsByTable = classification.SingleTableConjuncts
                .GroupBy(c => c.Table)
                .ToDictionary(g => g.Key, g => g.ToList());

            var defaultPushOptions = new PredicatePushdownOptions();

            foreach (var table in resolvedTables)
            {
                var pushedPredicates = new List<AnalyzedPushedPredicate>();
                var residualPredicates = new List<AnalyzedResidualPredicate>();
                var visibleMeta = visibleMetadataByTable[table.ResolvedIdentifier];

                if (table.Metadata.DataSourceType != DataSourceType.Sql)
                {
                    if (singleConjunctsByTable.TryGetValue(table.ResolvedIdentifier, out var tableConjuncts))
                    {
                        foreach (var sc in tableConjuncts)
                        {
                            foreach (var col in sc.Columns)
                            {
                                if (visibleMeta.HasColumn(col))
                                {
                                    residualPredicates.Add(new AnalyzedResidualPredicate(col, RejectionReason.NotSqlSource));
                                }
                            }
                        }
                    }
                }
                else
                {
                    var decision = ResolveFederationTableDecision(scopeContext, table.ResolvedIdentifier);

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
                                    if (visibleMeta.HasColumn(col))
                                    {
                                        residualPredicates.Add(new AnalyzedResidualPredicate(col, evalResult.RejectionReason ?? RejectionReason.UnsupportedExpression));
                                    }
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
                            if (visibleMeta.HasColumn(colName))
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
                keySetDecisions[table.ResolvedIdentifier] = ResolveFederationTableDecision(scopeContext, table.ResolvedIdentifier);
            }

            var keySetPlans = DynamicKeySetPlanner.Plan(
                parsedStatement,
                visibleResolvedTables,
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
            // Issue #123: Subtree pushdown analysis (parity with execution eligibility and subtree cap).
            if (parsedStatement != null && resolvedTables.Count >= 2)
            {
                var maxTablesPerSubtree = (_gatewayOptions.WebSql?.CrossSource ?? new CrossSourceOptions()).MaxTablesPerSubtree;
                var partitionResult = Autheris.Application.Sql.Federation.Subtrees.SubtreeGraphPartitioner.PartitionSubtrees(
                    parsedStatement,
                    resolvedTables,
                    maxTablesPerSubtree,
                    t => TryIsPushdownEligible(t, scopeContext.TableDecisions));
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

        var rowLimit = SqlRowLimit.For(_gatewayOptions.WebSql ?? new WebSqlOptions());
        outcome.EstimatedCost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            rowLimit,
            _gatewayOptions.QueryCost ?? new QueryCostOptions(),
            _analysisOptions,
            _gatewayOptions.WebSql?.Cost ?? new WebSqlCostOptions());
        outcome.EstimatedRows = metadata.HasExplicitLimit && metadata.ExplicitLimitValue.HasValue
            ? (int)Math.Min((long)int.MaxValue, metadata.ExplicitLimitValue.Value)
            : 1000;
        return outcome;
    }

    internal static bool TryIsPushdownEligible(
        ResolvedSourceTable table,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions) =>
        TryIsPushdownEligible(table, id => decisions.TryGetValue(id, out var d) ? d : null);

    internal static bool TryIsPushdownEligible(
        ResolvedSourceTable table,
        Func<TableIdentifier, TableAccessDecision?> lookupDecision)
    {
        try
        {
            var decision = lookupDecision(table.Metadata.Identifier);
            if (decision is null)
            {
                return false;
            }

            return SubtreeCompiler.IsPushdownEligible(table, decision);
        }
        catch
        {
            return false;
        }
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

    private static TableAccessDecision ResolveFederationTableDecision(
        AnalysisScopeContext scopeContext,
        TableIdentifier tableId) =>
        scopeContext.TableDecisions.TryGetValue(tableId, out var existing)
            ? existing
            : TableAccessDecision.Denied(tableId, "Missing access decision.");
}
