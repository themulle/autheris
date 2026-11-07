using System.Collections.Immutable;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RequestContext = HotChocolate.Execution.RequestContext;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

namespace Autheris.Api.Middleware;

/// <summary>
/// R-GQL-6 / R-ERR-1: Outside Development an unknown field, a denied table and a denied column must not be
/// distinguishable. HotChocolate 16 reports unknown fields as document validation errors, which never reach an
/// <see cref="IErrorFilter"/>, and without data; denied tables and columns are resolver errors with partial data.
/// This request middleware wraps validation and execution and replaces every such result with one identical,
/// generic error result (same body, same HTTP status 400).
/// </summary>
public sealed class GraphQlEnumerationShieldMiddleware
{
    internal const string Code = "INVALID_QUERY";
    internal const string Message = "The query is invalid or references fields that are not available.";

    /// <summary>Error codes raised by the catalog resolvers for denied tables and unknown or denied columns.</summary>
    private static readonly HashSet<string> CatalogAccessCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS_DENIED",
        "INVALID_QUERY"
    };

    internal const string ComplexCode = "QUERY_TOO_COMPLEX";

    /// <summary>Stable code of the execution depth rule.</summary>
    internal const string DepthCode = "QUERY_TOO_DEEP";

    private enum CatalogCostEvaluation
    {
        NotApplicable,
        Allowed,
        Denied
    }

    /// <summary>
    /// HotChocolate's max-execution-depth rule reports a code-less error with the <c>detectedExecutionDepth</c> extension; it is
    /// told apart by that key, not by message text.
    /// </summary>
    private static bool IsDepthError(IError error) =>
        error.Code is null && error.Extensions is { } extensions && extensions.ContainsKey("detectedExecutionDepth");

    private static bool IsCostError(IError error) =>
        string.Equals(error.Code, ComplexCode, StringComparison.OrdinalIgnoreCase);

    private static bool IsStructuralLimitError(IError error) =>
        IsDepthError(error) || IsCostError(error);

    /// <summary>
    /// #330 (09): the one resolver error that is not shielded outside Development, because it carries no schema information:
    /// code <c>INVALID_QUERY_OPTION</c> (or legacy <c>INVALID_QUERY</c>) with exactly the fixed page-range message (ordinal compare) of <see cref="GatewayPageRangeException"/>.
    /// </summary>
    internal static bool IsPageRangeError(IError error) =>
        (string.Equals(error.Code, "INVALID_QUERY_OPTION", StringComparison.Ordinal) || string.Equals(error.Code, Code, StringComparison.Ordinal))
        && string.Equals(error.Message, GatewayPageRangeException.FixedMessage, StringComparison.Ordinal);

    private readonly RequestDelegate _next;

    public GraphQlEnumerationShieldMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        if (context.Result is not OperationResult result || result.Errors is not { Count: > 0 } errors)
        {
            return;
        }

        // Unknown environment counts as production (fail-closed).
        var environment = context.RequestServices.GetService<IHostEnvironment>();
        bool isDevelopment = environment?.IsDevelopment() == true;
        bool validationFailed = result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors);

        // Issue #558: when a GraphQL document is otherwise valid (all validation errors are cost errors) and the caller may
        // read every catalog table and column it names, a cost over the limit answers QUERY_TOO_COMPLEX with the fixed
        // message and no numbers in every environment. An over-limit document that names an unknown field or a table/column
        // the caller may not read answers the single shielded error so that cost limits cannot be used as an existence oracle.
        if (validationFailed && errors.Any(IsStructuralLimitError))
        {
            var catalogEval = await EvaluateCatalogCostAccessAsync(context, errors, isDevelopment).ConfigureAwait(false);
            if (catalogEval == CatalogCostEvaluation.Allowed)
            {
                var data = result.ContextData;
                await result.DisposeAsync().ConfigureAwait(false);
                context.Result = new OperationResult(ImmutableList.Create(
                    ErrorBuilder.New().SetMessage(ErrorSanitizingFilter.ComplexMessage).SetCode(ComplexCode).Build()))
                {
                    ContextData = data
                };
                return;
            }

            if (catalogEval == CatalogCostEvaluation.NotApplicable && isDevelopment)
            {
                var limits = new List<IError>();
                if (errors.Any(IsDepthError))
                {
                    limits.Add(ErrorBuilder.New().SetMessage(ErrorSanitizingFilter.DepthMessage).SetCode(DepthCode).Build());
                }

                if (errors.Any(e => !IsDepthError(e) && IsStructuralLimitError(e)))
                {
                    limits.Add(ErrorBuilder.New().SetMessage(ErrorSanitizingFilter.ComplexMessage).SetCode(ComplexCode).Build());
                }

                var data = result.ContextData;
                await result.DisposeAsync().ConfigureAwait(false);
                context.Result = new OperationResult(limits.ToImmutableList()) { ContextData = data };
                return;
            }
        }

        // #330: a result whose every error is the page-range error answers that one fixed error (no path, locations or extensions) in
        // every environment; the table was authorized before it was raised. A mixed result (any other error) is shielded as before.
        if (!validationFailed && errors.All(IsPageRangeError))
        {
            await result.DisposeAsync().ConfigureAwait(false);
            context.Result = new OperationResult(ImmutableList.Create(
                ErrorBuilder.New().SetMessage(GatewayPageRangeException.FixedMessage).SetCode("INVALID_QUERY_OPTION").Build()))
            {
                ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.ValidationErrors, true)
            };
            return;
        }

        bool mustShield = validationFailed
            || (!isDevelopment && errors.Any(e => e.Code is not null && CatalogAccessCodes.Contains(e.Code)));

        if (!mustShield)
        {
            return;
        }

        var code = ErrorSanitizingFilter.ValidationCode;
        var message = ErrorSanitizingFilter.ValidationMessage;
        if (validationFailed && context.RequestServices.GetService<ErrorSanitizingFilter>() is { } filter)
        {
            var classified = filter.OnError(errors[0]);
            if (classified.Code is ErrorSanitizingFilter.ValidationCode or "GRAPHQL_PARSE_FAILED" or "GRAPHQL_VARIABLE_INVALID" && classified.Message is { } text)
            {
                code = classified.Code;
                message = text;
            }
        }

        await result.DisposeAsync().ConfigureAwait(false);
        context.Result = new OperationResult(ImmutableList.Create(
            ErrorBuilder.New().SetMessage(message).SetCode(code).Build()))
        {
            ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.ValidationErrors, true)
        };
    }

    private static async ValueTask<CatalogCostEvaluation> EvaluateCatalogCostAccessAsync(
        RequestContext context, IReadOnlyList<IError> errors, bool isDevelopment)
    {
        if (ExtractDocument(context) is not { } document || context.Schema?.QueryType is not { } queryType)
            return CatalogCostEvaluation.NotApplicable;

        var fragments = document.Definitions.OfType<FragmentDefinitionNode>().ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var opName = context.Request.OperationName;
        var operations = document.Definitions.OfType<OperationDefinitionNode>()
            .Where(op => string.IsNullOrEmpty(opName) || string.Equals(op.Name?.Value, opName, StringComparison.Ordinal)).ToList();
        if (operations.Count == 0) return CatalogCostEvaluation.NotApplicable;

        bool hasCatalogRootField = false, hasLegacyTableField = false;
        var catalogTables = new Dictionary<TableIdentifier, CatalogTableType>();
        var requestedColumns = new Dictionary<TableIdentifier, HashSet<string>>();
        var clearRequiredColumns = new Dictionary<TableIdentifier, HashSet<string>>();
        var legacyTables = new HashSet<TableIdentifier>();

        foreach (var op in operations.Where(o => o.Operation == OperationType.Query))
        {
            foreach (var rootField in SelectFields(op.SelectionSet, fragments))
            {
                var fieldName = rootField.Name.Value;
                if (fieldName.StartsWith("__", StringComparison.Ordinal)) continue;
                if (!queryType.Fields.TryGetField(fieldName, out var rootFieldDef))
                {
                    if (hasCatalogRootField || queryType.Fields.Any(f => f.Features.Get<CatalogRootFieldFeature>() is not null))
                        return CatalogCostEvaluation.Denied;
                    continue;
                }

                if (rootFieldDef.Features.Get<CatalogRootFieldFeature>() is { } feature)
                {
                    hasCatalogRootField = true;
                    if (!errors.All(IsCostError) || !TryCollectRootCatalogField(
                            rootField, feature.Table, feature.SchemaModel, fragments, catalogTables, requestedColumns, clearRequiredColumns))
                        return CatalogCostEvaluation.Denied;
                    continue;
                }

                if (string.Equals(fieldName, "table", StringComparison.Ordinal))
                {
                    hasLegacyTableField = true;
                    if (!errors.All(IsCostError) || !TryExtractLegacyTableIdentifier(rootField, out var legacyTableId))
                        return isDevelopment ? CatalogCostEvaluation.NotApplicable : CatalogCostEvaluation.Denied;
                    legacyTables.Add(legacyTableId);
                }
            }
        }

        if (!hasCatalogRootField && !hasLegacyTableField) return CatalogCostEvaluation.NotApplicable;
        if (!errors.All(IsCostError)) return CatalogCostEvaluation.Denied;
        if (context.RequestServices.GetService<ITableAccessResolver>() is not { } accessResolver)
            return isDevelopment ? CatalogCostEvaluation.Allowed : CatalogCostEvaluation.Denied;

        var (principal, headers) = ExtractCaller(context);
        foreach (var (tableId, _) in catalogTables)
        {
            var reqCols = requestedColumns.TryGetValue(tableId, out var rSet) ? rSet.ToList() : [];
            var clearCols = clearRequiredColumns.TryGetValue(tableId, out var cSet) ? cSet : null;
            ResolvedTableAccess resolved;
            try { resolved = await accessResolver.ResolveTableAccessAsync(principal, tableId, reqCols, headers, context.RequestAborted).ConfigureAwait(false); }
            catch { return CatalogCostEvaluation.Denied; }

            if (!resolved.Decision.IsAllowed) return CatalogCostEvaluation.Denied;
            if (reqCols.Any(col => resolved.Metadata.GetColumn(col) is not { } cc || resolved.Decision.GetEffectiveColumnAccess(cc.ColumnName, resolved.Metadata) == ColumnAccessLevel.Deny))
                return CatalogCostEvaluation.Denied;
            if (clearCols is { Count: > 0 } && clearCols.Any(col => resolved.Metadata.GetColumn(col) is not { } cc || resolved.Decision.GetEffectiveColumnAccess(cc.ColumnName, resolved.Metadata) != ColumnAccessLevel.Clear))
                return CatalogCostEvaluation.Denied;
            if (reqCols.Count == 0 && !resolved.Metadata.Columns.Any(c => resolved.Decision.GetEffectiveColumnAccess(c.ColumnName, resolved.Metadata) != ColumnAccessLevel.Deny))
                return CatalogCostEvaluation.Denied;
        }

        foreach (var legacyTableId in legacyTables)
        {
            try
            {
                var resolved = await accessResolver.ResolveTableAccessAsync(principal, legacyTableId, null, headers, context.RequestAborted).ConfigureAwait(false);
                if (!resolved.Decision.IsAllowed)
                    return isDevelopment && !hasCatalogRootField ? CatalogCostEvaluation.NotApplicable : CatalogCostEvaluation.Denied;
            }
            catch { return isDevelopment && !hasCatalogRootField ? CatalogCostEvaluation.NotApplicable : CatalogCostEvaluation.Denied; }
        }

        return CatalogCostEvaluation.Allowed;
    }

    private static DocumentNode? ExtractDocument(RequestContext context)
    {
        if (context.OperationDocumentInfo?.Document is { } doc) return doc;
        if (context.Request.Document is IOperationDocumentNodeProvider np) return np.Document;
        try { var t = context.Request.Document?.ToString(); return string.IsNullOrWhiteSpace(t) ? null : Utf8GraphQLParser.Parse(t); }
        catch { return null; }
    }

    private static (ClaimsPrincipal? Principal, IReadOnlyDictionary<string, string[]>? Headers) ExtractCaller(RequestContext context)
    {
        var hc = (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext h) ? h : context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
        var principal = (context.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cpObj) && cpObj is ClaimsPrincipal cp) ? cp : hc?.User;
        var headers = hc?.Request?.Headers is { Count: > 0 } rh ? rh.ToDictionary(x => x.Key, x => x.Value.Where(v => v != null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase) : null;
        return (principal, headers);
    }

    private static bool TryExtractLegacyTableIdentifier(FieldNode field, out TableIdentifier tableId)
    {
        tableId = default;
        var domain = ReadStringArgument(field, "domain");
        var name = ReadStringArgument(field, "name");
        return !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(name)
            && TableIdentifier.TryCreate(domain, ReadStringArgument(field, "schema") ?? "dbo", name, out tableId);
    }

    private static string? ReadStringArgument(FieldNode field, string argName) =>
        field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, argName, StringComparison.Ordinal))?.Value is StringValueNode svn ? svn.Value : null;

    private static bool TryCollectRootCatalogField(
        FieldNode rootField, CatalogTableType table, CatalogSchemaModel schemaModel,
        Dictionary<string, FragmentDefinitionNode> fragments, Dictionary<TableIdentifier, CatalogTableType> catalogTables,
        Dictionary<TableIdentifier, HashSet<string>> requestedColumns, Dictionary<TableIdentifier, HashSet<string>> clearRequiredColumns)
    {
        var fieldName = rootField.Name.Value;
        if (string.Equals(fieldName, $"{table.QueryFieldName}Connection", StringComparison.Ordinal))
        {
            EnsureTableSets(table, catalogTables, requestedColumns, clearRequiredColumns, out var reqCols, out var clearCols);
            if (!TryCollectWhereAndOrderColumns(rootField, table, reqCols, clearCols) || rootField.SelectionSet is null) return false;
            foreach (var pk in table.Metadata.PrimaryKeyColumns)
                reqCols.Add(table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, pk, StringComparison.OrdinalIgnoreCase))?.ColumnName ?? pk);

            foreach (var cf in SelectFields(rootField.SelectionSet, fragments).Where(f => string.Equals(f.Name.Value, "edges", StringComparison.Ordinal) && f.SelectionSet is not null))
                foreach (var ef in SelectFields(cf.SelectionSet!, fragments).Where(f => string.Equals(f.Name.Value, "node", StringComparison.Ordinal) && f.SelectionSet is not null))
                    if (!TryCollectSelectionSet(ef.SelectionSet!, table, schemaModel, fragments, catalogTables, requestedColumns, clearRequiredColumns, 1))
                        return false;
            return true;
        }

        if (string.Equals(fieldName, $"{table.QueryFieldName}_aggregate", StringComparison.Ordinal))
        {
            EnsureTableSets(table, catalogTables, requestedColumns, clearRequiredColumns, out var reqCols, out var clearCols);
            if (!TryCollectWhereAndOrderColumns(rootField, table, reqCols, clearCols)) return false;
            var groupByArg = rootField.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "groupBy", StringComparison.Ordinal));
            if (groupByArg is not null && !TryCollectGroupByArgument(groupByArg.Value, table, reqCols, clearCols)) return false;
            if (rootField.SelectionSet is not null)
            {
                foreach (var af in SelectFields(rootField.SelectionSet, fragments).Where(f => f.Name.Value is "groupBy" or "sum" or "avg" or "min" or "max" && f.SelectionSet is not null))
                    foreach (var cf in SelectFields(af.SelectionSet!, fragments).Where(f => !f.Name.Value.StartsWith("__", StringComparison.Ordinal)))
                    {
                        var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, cf.Name.Value, StringComparison.Ordinal));
                        if (col is null) return false;
                        reqCols.Add(col.ColumnName);
                        clearCols.Add(col.ColumnName);
                    }
            }
            return true;
        }

        return TryCollectTableField(rootField, table, schemaModel, fragments, catalogTables, requestedColumns, clearRequiredColumns, 0);
    }

    private static bool TryCollectTableField(
        FieldNode field, CatalogTableType table, CatalogSchemaModel schemaModel,
        Dictionary<string, FragmentDefinitionNode> fragments, Dictionary<TableIdentifier, CatalogTableType> catalogTables,
        Dictionary<TableIdentifier, HashSet<string>> requestedColumns, Dictionary<TableIdentifier, HashSet<string>> clearRequiredColumns, int depth)
    {
        if (depth > 10 || field.SelectionSet is null) return false;
        EnsureTableSets(table, catalogTables, requestedColumns, clearRequiredColumns, out var reqCols, out var clearCols);
        return TryCollectWhereAndOrderColumns(field, table, reqCols, clearCols)
            && TryCollectSelectionSet(field.SelectionSet, table, schemaModel, fragments, catalogTables, requestedColumns, clearRequiredColumns, depth);
    }

    private static bool TryCollectSelectionSet(
        SelectionSetNode selectionSet, CatalogTableType table, CatalogSchemaModel schemaModel,
        Dictionary<string, FragmentDefinitionNode> fragments, Dictionary<TableIdentifier, CatalogTableType> catalogTables,
        Dictionary<TableIdentifier, HashSet<string>> requestedColumns, Dictionary<TableIdentifier, HashSet<string>> clearRequiredColumns, int depth)
    {
        EnsureTableSets(table, catalogTables, requestedColumns, clearRequiredColumns, out var reqCols, out var clearCols);
        foreach (var selField in SelectFields(selectionSet, fragments))
        {
            var name = selField.Name.Value;
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            if (table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, name, StringComparison.Ordinal)) is { } col) { reqCols.Add(col.ColumnName); continue; }
            var rel = table.Relations.FirstOrDefault(r => string.Equals(r.FieldName, name, StringComparison.Ordinal));
            if (rel is not null && schemaModel.TablesByIdentifier.TryGetValue(rel.TargetTableIdentifier, out var targetTable))
            {
                foreach (var parentCol in rel.ParentColumns) { reqCols.Add(parentCol); clearCols.Add(parentCol); }
                EnsureTableSets(targetTable, catalogTables, requestedColumns, clearRequiredColumns, out var childReqCols, out var childClearCols);
                foreach (var childCol in rel.ChildColumns) { childReqCols.Add(childCol); childClearCols.Add(childCol); }
                if (!TryCollectTableField(selField, targetTable, schemaModel, fragments, catalogTables, requestedColumns, clearRequiredColumns, depth + 1)) return false;
                continue;
            }
            return false;
        }
        return true;
    }

    private static List<FieldNode> SelectFields(SelectionSetNode selectionSet, Dictionary<string, FragmentDefinitionNode> fragments)
    {
        var list = new List<FieldNode>();
        CollectFieldNodes(selectionSet, fragments, new HashSet<string>(StringComparer.Ordinal), list);
        return list;
    }

    private static void EnsureTableSets(
        CatalogTableType table, Dictionary<TableIdentifier, CatalogTableType> catalogTables,
        Dictionary<TableIdentifier, HashSet<string>> requestedColumns, Dictionary<TableIdentifier, HashSet<string>> clearRequiredColumns,
        out HashSet<string> reqCols, out HashSet<string> clearCols)
    {
        catalogTables[table.Identifier] = table;
        if (!requestedColumns.TryGetValue(table.Identifier, out reqCols!))
            requestedColumns[table.Identifier] = reqCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!clearRequiredColumns.TryGetValue(table.Identifier, out clearCols!))
            clearRequiredColumns[table.Identifier] = clearCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryCollectWhereAndOrderColumns(FieldNode field, CatalogTableType table, HashSet<string> reqCols, HashSet<string> clearCols)
    {
        var whereArg = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "where", StringComparison.Ordinal));
        if (whereArg is not null && !TryCollectWhereFilter(whereArg.Value, table, reqCols, clearCols)) return false;
        var orderArg = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "orderBy", StringComparison.Ordinal));
        return orderArg is null || TryCollectOrderBy(orderArg.Value, table, reqCols, clearCols);
    }

    private static bool TryCollectWhereFilter(IValueNode valueNode, CatalogTableType table, HashSet<string> reqCols, HashSet<string> clearCols)
    {
        if (valueNode is NullValueNode or VariableNode) return true;
        if (valueNode is not ObjectValueNode obj) return false;
        foreach (var field in obj.Fields)
        {
            var name = field.Name.Value;
            if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "or", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Value is ListValueNode list && list.Items.Any(i => !TryCollectWhereFilter(i, table, reqCols, clearCols)))
                    return false;
                continue;
            }
            if (string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryCollectWhereFilter(field.Value, table, reqCols, clearCols)) return false;
                continue;
            }
            var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, name, StringComparison.Ordinal));
            if (col is null) return false;
            reqCols.Add(col.ColumnName);
            clearCols.Add(col.ColumnName);
        }
        return true;
    }

    private static bool TryCollectOrderBy(IValueNode valueNode, CatalogTableType table, HashSet<string> reqCols, HashSet<string> clearCols)
    {
        if (valueNode is NullValueNode or VariableNode) return true;
        if (valueNode is ListValueNode list) return list.Items.All(i => TryCollectOrderBy(i, table, reqCols, clearCols));
        if (valueNode is not ObjectValueNode obj) return false;
        foreach (var field in obj.Fields)
        {
            var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, field.Name.Value, StringComparison.Ordinal));
            if (col is null) return false;
            reqCols.Add(col.ColumnName);
            clearCols.Add(col.ColumnName);
        }
        return true;
    }

    private static bool TryCollectGroupByArgument(IValueNode valueNode, CatalogTableType table, HashSet<string> reqCols, HashSet<string> clearCols)
    {
        if (valueNode is NullValueNode or VariableNode) return true;
        if (valueNode is ListValueNode list) return list.Items.All(i => TryCollectGroupByArgument(i, table, reqCols, clearCols));
        var token = valueNode switch { EnumValueNode evn => evn.Value, StringValueNode svn => svn.Value, _ => null };
        if (token is null) return false;
        var col = table.Columns.FirstOrDefault(c =>
            string.Equals(c.FieldName, token, StringComparison.OrdinalIgnoreCase) || string.Equals(c.ColumnName, token, StringComparison.OrdinalIgnoreCase));
        if (col is null) return false;
        reqCols.Add(col.ColumnName);
        clearCols.Add(col.ColumnName);
        return true;
    }

    private static void CollectFieldNodes(
        SelectionSetNode selectionSet, Dictionary<string, FragmentDefinitionNode> fragments, HashSet<string> visitedFragments, List<FieldNode> fields)
    {
        foreach (var selection in selectionSet.Selections)
        {
            switch (selection)
            {
                case FieldNode field: fields.Add(field); break;
                case InlineFragmentNode inline: CollectFieldNodes(inline.SelectionSet, fragments, visitedFragments, fields); break;
                case FragmentSpreadNode spread when fragments.TryGetValue(spread.Name.Value, out var frag) && visitedFragments.Add(spread.Name.Value):
                    CollectFieldNodes(frag.SelectionSet, fragments, visitedFragments, fields);
                    visitedFragments.Remove(spread.Name.Value);
                    break;
            }
        }
    }
}
