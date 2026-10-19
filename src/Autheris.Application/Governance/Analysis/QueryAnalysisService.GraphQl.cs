namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate.Language;

public sealed partial class QueryAnalysisService
{
    private static void VerifyOrPopulateDeclaredReferencedTables(
        string queryName,
        string definitionName,
        IReadOnlyList<string>? declaredTables,
        IReadOnlyList<string> astExtractedTables,
        List<QueryValidationDiagnostic> diagnostics,
        Dictionary<string, IReadOnlyList<string>> populatedReferencedTables)
    {
        var normalizedAst = astExtractedTables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (declaredTables == null || declaredTables.Count == 0)
        {
            populatedReferencedTables[definitionName] = normalizedAst;
            return;
        }

        var declaredSet = new HashSet<string>(
            declaredTables.Select(ExtractUnqualifiedName),
            StringComparer.OrdinalIgnoreCase);
        var astSet = new HashSet<string>(
            normalizedAst.Select(ExtractUnqualifiedName),
            StringComparer.OrdinalIgnoreCase);

        if (!declaredSet.SetEquals(astSet))
        {
            var undeclared = astSet.Except(declaredSet, StringComparer.OrdinalIgnoreCase).ToList();
            var unused = declaredSet.Except(astSet, StringComparer.OrdinalIgnoreCase).ToList();
            diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.ReferencedTablesMismatch,
                $"Declared ReferencedTables [{string.Join(", ", declaredTables)}] on '{definitionName}' do not match AST-extracted tables [{string.Join(", ", normalizedAst)}] (undeclared: [{string.Join(", ", undeclared)}], unused: [{string.Join(", ", unused)}]).",
                QueryName: queryName));
        }
    }

    private async Task<SingleQueryAnalysisOutcome> AnalyzeDeclaredTablesOnlyAsync(
        IReadOnlyList<string> declaredTables,
        IReadOnlyList<string> declaredOutputs,
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
        var outcome = new SingleQueryAnalysisOutcome
        {
            EstimatedCost = Math.Max(1, declaredTables.Count) * _analysisOptions.CostPerRequestedField
        };

        foreach (var rawTable in declaredTables)
        {
            outcome.ExtractedTables.Add(rawTable);
            await EvaluateTableAndColumnsAsync(
                rawTable,
                requestedColumns: declaredOutputs,
                filterColumns: Array.Empty<string>(),
                providedFilterColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                queryName,
                effectivePrincipal,
                effectiveSid,
                isAdmin,
                scopeContext,
                activeConsents,
                sunsetRules,
                tableLookup,
                selectAllColumns: declaredOutputs.Count == 0,
                outcome,
                ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<SingleQueryAnalysisOutcome> AnalyzeGraphQlItemAsync(
        string graphQlQuery,
        IReadOnlyDictionary<string, object?>? variables,
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
        DocumentNode document;
        try
        {
            document = Utf8GraphQLParser.Parse(graphQlQuery);
        }
        catch (SyntaxException ex)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                $"GraphQL syntax error: {ex.Message}",
                QueryName: queryName));
            return outcome;
        }

        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count == 0)
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.SyntaxError,
                "GraphQL document does not contain any executable operation.",
                QueryName: queryName));
            return outcome;
        }

        var totalFields = 0;
        var maxDepth = 0;

        foreach (var op in operations)
        {
            foreach (var rootField in op.SelectionSet.Selections.OfType<FieldNode>())
            {
                var fieldName = rootField.Name.Value;
                if (fieldName.StartsWith("__", StringComparison.Ordinal))
                {
                    continue;
                }

                outcome.ExtractedTables.Add(fieldName);

                var requestedColumns = new List<string>();
                var filterColumns = new List<string>();
                var providedFilterColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                ExtractGraphQlFilterArguments(rootField.Arguments, variables, filterColumns, providedFilterColumns);
                CollectSelectedGraphQlFields(rootField.SelectionSet, requestedColumns, currentDepth: 1, ref maxDepth, ref totalFields);

                await EvaluateTableAndColumnsAsync(
                    fieldName,
                    requestedColumns,
                    filterColumns,
                    providedFilterColumns,
                    queryName,
                    effectivePrincipal,
                    effectiveSid,
                    isAdmin,
                    scopeContext,
                    activeConsents,
                    sunsetRules,
                    tableLookup,
                    selectAllColumns: false,
                    outcome,
                    ct).ConfigureAwait(false);
            }
        }

        if (_costAnalysisEngine != null)
        {
            var costResult = await _costAnalysisEngine.CalculateCostAsync(document, variables, ct).ConfigureAwait(false);
            outcome.EstimatedCost = costResult.TotalCost;
            foreach (var (rfName, rfCost) in costResult.RootFieldCosts)
            {
                outcome.RootFieldCosts[rfName] = rfCost;
            }

            if (costResult.MaxRootFieldsCount > _maxRootFields)
            {
                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.QueryTooComplex,
                    $"The query exceeds the maximum of {_maxRootFields} root fields or aliases per operation.",
                    QueryName: queryName));
            }
        }
        else
        {
            outcome.EstimatedCost = Math.Max(1, totalFields) * Math.Max(1, _analysisOptions.CostPerRequestedField)
                + Math.Max(0, maxDepth - 1) * _analysisOptions.CostPerJoin;
        }

        return outcome;
    }

    private static void ExtractGraphQlFilterArguments(
        IReadOnlyList<ArgumentNode> arguments,
        IReadOnlyDictionary<string, object?>? variables,
        List<string> filterColumns,
        HashSet<string> providedFilterColumns)
    {
        foreach (var arg in arguments)
        {
            var argName = arg.Name.Value;
            if (string.Equals(argName, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "filter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "orderBy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "order_by", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "sort", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argName, "sortBy", StringComparison.OrdinalIgnoreCase))
            {
                ExtractFilterFieldsFromValueNode(arg.Value, variables, filterColumns, providedFilterColumns);
            }
            else
            {
                providedFilterColumns.Add(argName);
            }
        }
    }

    private static void ExtractFilterFieldsFromValueNode(
        IValueNode valueNode,
        IReadOnlyDictionary<string, object?>? variables,
        List<string> filterColumns,
        HashSet<string> providedFilterColumns)
    {
        if (valueNode is ObjectValueNode objNode)
        {
            foreach (var field in objNode.Fields)
            {
                var name = field.Name.Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "or", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractFilterFieldsFromValueNode(field.Value, variables, filterColumns, providedFilterColumns);
                    continue;
                }

                var baseCol = StripGraphQlOperatorSuffix(name);
                filterColumns.Add(baseCol);
                providedFilterColumns.Add(baseCol);
            }
        }
        else if (valueNode is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                ExtractFilterFieldsFromValueNode(item, variables, filterColumns, providedFilterColumns);
            }
        }
        else if (valueNode is StringValueNode strNode)
        {
            var baseCol = StripGraphQlOperatorSuffix(strNode.Value);
            filterColumns.Add(baseCol);
            providedFilterColumns.Add(baseCol);
        }
        else if (valueNode is EnumValueNode enumNode)
        {
            var baseCol = StripGraphQlOperatorSuffix(enumNode.Value);
            filterColumns.Add(baseCol);
            providedFilterColumns.Add(baseCol);
        }
        else if (valueNode is VariableNode varNode &&
                 variables != null &&
                 variables.TryGetValue(varNode.Name.Value, out var varObj))
        {
            if (varObj is IEnumerable<KeyValuePair<string, object?>> dict)
            {
                foreach (var kvp in dict)
                {
                    var baseCol = StripGraphQlOperatorSuffix(kvp.Key);
                    filterColumns.Add(baseCol);
                    providedFilterColumns.Add(baseCol);
                }
            }
            else if (varObj is string strVal)
            {
                var baseCol = StripGraphQlOperatorSuffix(strVal);
                filterColumns.Add(baseCol);
                providedFilterColumns.Add(baseCol);
            }
            else if (varObj is IEnumerable<object?> listVal)
            {
                foreach (var item in listVal)
                {
                    if (item is string itemStr)
                    {
                        var baseCol = StripGraphQlOperatorSuffix(itemStr);
                        filterColumns.Add(baseCol);
                        providedFilterColumns.Add(baseCol);
                    }
                    else if (item is IEnumerable<KeyValuePair<string, object?>> itemDict)
                    {
                        foreach (var kvp in itemDict)
                        {
                            var baseCol = StripGraphQlOperatorSuffix(kvp.Key);
                            filterColumns.Add(baseCol);
                            providedFilterColumns.Add(baseCol);
                        }
                    }
                }
            }
        }
    }

    private static string StripGraphQlOperatorSuffix(string fieldName)
    {
        string[] suffixes = ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_in", "_nin", "_contains", "_startsWith", "_endsWith"];
        foreach (var suffix in suffixes)
        {
            if (fieldName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fieldName.Length > suffix.Length)
            {
                return fieldName[..^suffix.Length];
            }
        }

        return fieldName;
    }

    private static void CollectSelectedGraphQlFields(
        SelectionSetNode? selectionSet,
        List<string> columns,
        int currentDepth,
        ref int maxDepth,
        ref int totalFields)
    {
        if (selectionSet == null || currentDepth > 25)
        {
            return;
        }

        if (currentDepth > maxDepth)
        {
            maxDepth = currentDepth;
        }

        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode field)
            {
                var name = field.Name.Value;
                if (name.StartsWith("__", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(name, "nodes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "edges", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "node", StringComparison.OrdinalIgnoreCase))
                {
                    CollectSelectedGraphQlFields(field.SelectionSet, columns, currentDepth + 1, ref maxDepth, ref totalFields);
                    continue;
                }

                totalFields++;
                if (field.SelectionSet == null || field.SelectionSet.Selections.Count == 0)
                {
                    columns.Add(name);
                }
                else
                {
                    CollectSelectedGraphQlFields(field.SelectionSet, columns, currentDepth + 1, ref maxDepth, ref totalFields);
                }
            }
        }
    }
}
