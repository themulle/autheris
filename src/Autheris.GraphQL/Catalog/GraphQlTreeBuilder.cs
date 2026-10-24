using System.Globalization;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using HotChocolate.Resolvers;

namespace Autheris.GraphQL.Catalog;

public static class GraphQlTreeBuilder
{
    /// <summary>Builds the tree; a page size or offset out of range throws <see cref="GatewayPageRangeException"/> (#330, SEC-RG-07).</summary>
    public static TreeQueryNode BuildTree(
        IResolverContext context,
        CatalogTableType rootTable,
        CatalogSchemaModel schema,
        int maxResponseRows)
    {
        var root = BuildTree(context, rootTable, schema, maxResponseRows, out var pageRangeViolated);
        if (pageRangeViolated)
        {
            throw new GatewayPageRangeException();
        }

        return root;
    }

    /// <summary>
    /// Builds the tree and reports an out-of-range page size or offset instead of throwing: the violating node is clamped to a zero-row page,
    /// so the caller can run table authorization first and only then answer with <see cref="GatewayPageRangeException"/> (SEC-RG-08: a denied
    /// table must answer identically for every page size).
    /// </summary>
    public static TreeQueryNode BuildTree(
        IResolverContext context,
        CatalogTableType rootTable,
        CatalogSchemaModel schema,
        int maxResponseRows,
        out bool pageRangeViolated)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rootTable);
        ArgumentNullException.ThrowIfNull(schema);

        var selection = context.Selection;
        var operation = context.Operation;

        var gqlOptions = context.Services.GetService(typeof(Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>)) as Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>;
        var maxBudget = gqlOptions?.Value?.GraphQL?.MaxAggregateRowBudget > 0
            ? gqlOptions.Value.GraphQL.MaxAggregateRowBudget
            : MaxAggregateBudget;
        var maxAllowedOffset = gqlOptions?.Value?.GraphQL?.MaxAllowedOffset >= 0
            ? gqlOptions.Value.GraphQL.MaxAllowedOffset
            : 10_000;

        var tracker = new PageRangeTracker();
        var root = BuildNode(context, selection, operation, rootTable, schema, maxResponseRows, isRoot: true, maxAllowedOffset: maxAllowedOffset, tracker: tracker);
        ValidateTreeBudget(root, maxBudget);
        pageRangeViolated = tracker.Violated;
        return root;
    }

    /// <summary>The fixed client message of a page size or offset outside its allowed range; the only source is <see cref="GatewayPageRangeException"/>.</summary>
    public const string PageRangeMessage = GatewayPageRangeException.FixedMessage;

    private sealed class PageRangeTracker
    {
        public bool Violated { get; set; }
    }

    public const int MaxAggregateBudget = 50_000;

    private static void ValidateTreeBudget(TreeQueryNode root, int maxBudget = MaxAggregateBudget)
    {
        long estimatedRows = EstimateRows(root, 1, isList: true);
        if (estimatedRows > maxBudget)
        {
            throw new GatewayInvalidQueryException($"The query exceeds the aggregate row budget of {maxBudget} across nested relations (estimated worst-case rows: {estimatedRows}).");
        }
    }

    private static long EstimateRows(TreeQueryNode node, long parentMultiplier, bool isList = true)
    {
        try
        {
            checked
            {
                long currentRows = isList ? parentMultiplier * node.Limit : parentMultiplier;
                long total = currentRows;
                foreach (var rel in node.Relations)
                {
                    total += EstimateRows(rel.Child, currentRows, rel.IsList);
                }
                return total;
            }
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static TreeQueryNode BuildNode(
        IResolverContext context,
        Selection currentSelection,
        Operation operation,
        CatalogTableType currentTable,
        CatalogSchemaModel schema,
        int maxAllowedLimit,
        bool isRoot,
        int maxAllowedOffset = 10_000,
        int depth = 0,
        PageRangeTracker? tracker = null)
    {
        if (depth > 10)
        {
            throw new GatewayInvalidQueryException("Query nesting depth exceeds maximum permitted limit of 10.");
        }

        // 1. Parse Arguments (where, orderBy, first, offset)
        var whereFilter = ParseWhereArgument(currentSelection, currentTable, context);
        var orderByList = ParseOrderByArgument(currentSelection, currentTable, context);

        int limit = isRoot ? Math.Min(100, maxAllowedLimit) : 100;
        int offset = 0;
        bool pageRangeViolated = false;

        // #318: a null or absent first is the default page (never unbounded); a null offset is 0. Coerced variable values of any
        // stored representation are read. #330 (SEC-RG-07): the range check covers literals and variables alike and saturates on
        // overflow, so an out-of-range value is a fixed page-range error, never an exception.
        if (currentSelection.Arguments.TryGetValue("first", out var firstArg) && firstArg?.ValueLiteral != null)
        {
            var first = ReadPageNumber(firstArg.ValueLiteral, context);
            if (first.Invalid)
            {
                pageRangeViolated = true;
            }
            else if (first.Value is { } firstValue)
            {
                if (firstValue < 0 || firstValue > maxAllowedLimit)
                {
                    pageRangeViolated = true;
                }
                else
                {
                    limit = (int)firstValue;
                }
            }
        }

        if (currentSelection.Arguments.TryGetValue("offset", out var offsetArg) && offsetArg?.ValueLiteral != null)
        {
            var offsetNumber = ReadPageNumber(offsetArg.ValueLiteral, context);
            if (offsetNumber.Invalid)
            {
                pageRangeViolated = true;
            }
            else if (offsetNumber.Value is { } offsetValue)
            {
                if (offsetValue < 0 || offsetValue > maxAllowedOffset)
                {
                    pageRangeViolated = true;
                }
                else
                {
                    offset = (int)offsetValue;
                }
            }
        }

        if (pageRangeViolated)
        {
            if (tracker is null)
            {
                throw new GatewayPageRangeException();
            }

            // Clamp to a zero-row page so the caller can authorize before it answers (SEC-RG-08).
            tracker.Violated = true;
            limit = 0;
            offset = 0;
        }

        if (!isRoot && offset > 0)
        {
            throw new GatewayInvalidQueryException("Offset paging is only supported on root table queries.");
        }

        // 2. Inspect child selections
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relations = new List<TreeRelationNode>();

        var selectionSet = operation.GetSelectionSet(currentSelection);
        var selections = selectionSet.Selections;

        for (int i = 0; i < selections.Length; i++)
        {
            var sel = selections[i];
            if (!sel.IsIncluded(context.IncludeFlags))
            {
                continue;
            }

            var fieldName = sel.Field.Name;
            if (fieldName.StartsWith("__", StringComparison.Ordinal))
            {
                continue; // Skip __typename and introspection
            }

            // Check if column field
            var col = currentTable.Columns.FirstOrDefault(c => string.Equals(c.FieldName, fieldName, StringComparison.Ordinal));
            if (col != null)
            {
                columns.Add(col.ColumnName);
                continue;
            }

            // Check if relation field
            var rel = currentTable.Relations.FirstOrDefault(r => string.Equals(r.FieldName, fieldName, StringComparison.Ordinal));
            if (rel != null && schema.TablesByIdentifier.TryGetValue(rel.TargetTableIdentifier, out var targetTable))
            {
                var childNode = BuildNode(
                    context,
                    sel,
                    operation,
                    targetTable,
                    schema,
                    TreeSqlCompiler.MaxLimit,
                    isRoot: false,
                    depth: depth + 1,
                    tracker: tracker);

                relations.Add(new TreeRelationNode(
                    ResponseKey: sel.ResponseName,
                    ParentColumns: rel.ParentColumns,
                    ChildColumns: rel.ChildColumns,
                    IsList: rel.IsList,
                    Child: childNode));
            }
        }

        int executionLimit = isRoot ? Math.Min(limit + 1, maxAllowedLimit + 1) : limit;

        return new TreeQueryNode(currentTable.Identifier, columns.ToList())
        {
            Relations = relations,
            Where = whereFilter,
            OrderBy = orderByList,
            Limit = limit,
            Offset = offset,
            ExecutionLimit = executionLimit
        };
    }

    private static TreeFilter? ParseWhereArgument(
        Selection selection,
        CatalogTableType table,
        IResolverContext context)
    {
        if (!selection.Arguments.TryGetValue("where", out var whereArg) || whereArg?.ValueLiteral == null)
        {
            return null;
        }

        var literal = whereArg.ValueLiteral;
        if (literal is VariableNode varNode)
        {
            literal = ResolveVariableLiteral(varNode, context);
        }

        if (literal is not ObjectValueNode objNode || objNode.Fields.Count == 0)
        {
            return null;
        }

        return ParseObjectFilter(objNode, table, context);
    }

    internal static TreeFilter? ParseObjectFilter(
        ObjectValueNode objNode,
        CatalogTableType table,
        IResolverContext context)
    {
        var items = new List<TreeFilter>();

        foreach (var field in objNode.Fields)
        {
            var fieldName = field.Name.Value;
            var valLiteral = field.Value;
            if (valLiteral is VariableNode vn)
            {
                valLiteral = ResolveVariableLiteral(vn, context);
            }

            if (string.Equals(fieldName, "and", StringComparison.OrdinalIgnoreCase))
            {
                if (valLiteral is ListValueNode listNode)
                {
                    var andItems = new List<TreeFilter>();
                    foreach (var item in listNode.Items)
                    {
                        var itemLiteral = item is VariableNode ivn ? ResolveVariableLiteral(ivn, context) : item;
                        if (itemLiteral is NullValueNode)
                        {
                            // SEC-RG-11: a null member is rejected, never silently dropped.
                            throw new GatewayInvalidQueryException("The 'and' filter members cannot be null.");
                        }

                        if (itemLiteral is ObjectValueNode ovn)
                        {
                            var parsed = ParseObjectFilter(ovn, table, context);
                            if (parsed != null) andItems.Add(parsed);
                        }
                    }
                    if (andItems.Count > 0) items.Add(new TreeAndFilter(andItems));
                }
                continue;
            }

            if (string.Equals(fieldName, "or", StringComparison.OrdinalIgnoreCase))
            {
                if (valLiteral is NullValueNode)
                {
                    throw new GatewayInvalidQueryException("The 'or' filter argument cannot be null.");
                }

                if (valLiteral is ListValueNode listNode)
                {
                    if (listNode.Items.Count == 0)
                    {
                        items.Add(new TreeOrFilter([]));
                    }
                    else
                    {
                        var orItems = new List<TreeFilter>();
                        var orIsNeutral = false;
                        foreach (var item in listNode.Items)
                        {
                            var itemLiteral = item is VariableNode ivn ? ResolveVariableLiteral(ivn, context) : item;
                            if (itemLiteral is NullValueNode)
                            {
                                // SEC-RG-11: a null member is rejected like 'or: null', never silently dropped.
                                throw new GatewayInvalidQueryException("The 'or' filter members cannot be null.");
                            }

                            if (itemLiteral is ObjectValueNode ovn)
                            {
                                var parsed = ParseObjectFilter(ovn, table, context);
                                if (parsed != null)
                                {
                                    orItems.Add(parsed);
                                }
                                else
                                {
                                    // #319: an empty member matches every row, so the whole 'or' is neutral (TRUE OR x is TRUE).
                                    orIsNeutral = true;
                                }
                            }
                        }
                        if (!orIsNeutral && orItems.Count > 0) items.Add(new TreeOrFilter(orItems));
                    }
                }
                continue;
            }

            if (string.Equals(fieldName, "not", StringComparison.OrdinalIgnoreCase))
            {
                if (valLiteral is NullValueNode)
                {
                    throw new GatewayInvalidQueryException("The 'not' filter argument cannot be null.");
                }

                if (valLiteral is ObjectValueNode notObj)
                {
                    // G-9: an empty filter matches every row, so not: {} matches none (it used to filter nothing).
                    var parsed = ParseObjectFilter(notObj, table, context);
                    items.Add(parsed != null ? new TreeNotFilter(parsed) : new TreeOrFilter([]));
                }
                continue;
            }

            // Column filter
            var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, fieldName, StringComparison.Ordinal));
            if (col != null)
            {
                if (valLiteral is NullValueNode)
                {
                    throw new GatewayInvalidQueryException($"Column filter for '{fieldName}' cannot be null. Use '{fieldName}: {{ isNull: true }}'.");
                }

                if (valLiteral is ObjectValueNode colOps)
                {
                    foreach (var opField in colOps.Fields)
                    {
                        var opName = opField.Name.Value.ToLowerInvariant();
                        var opValLiteral = opField.Value is VariableNode opVn ? ResolveVariableLiteral(opVn, context) : opField.Value;
                        // G-9: also isNull: null is rejected; it used to become IS NOT NULL. #300: the comparison operators accept an
                        // explicit null operand (bound NULL, matches no row).
                        if (opValLiteral is NullValueNode && string.Equals(opName, "isnull", StringComparison.Ordinal))
                        {
                            throw new GatewayInvalidQueryException($"Value for operator '{opName}' on field '{fieldName}' cannot be null.");
                        }

                        var rawVal = ResolveValue(opValLiteral, context);

                    var filterOp = opName switch
                    {
                        "eq" => TreeFilterOperator.Eq,
                        "neq" => TreeFilterOperator.Neq,
                        "gt" => TreeFilterOperator.Gt,
                        "gte" => TreeFilterOperator.Gte,
                        "lt" => TreeFilterOperator.Lt,
                        "lte" => TreeFilterOperator.Lte,
                        "in" => TreeFilterOperator.In,
                        "nin" => TreeFilterOperator.NotIn,
                        "contains" => TreeFilterOperator.Contains,
                        "startswith" => TreeFilterOperator.StartsWith,
                        "endswith" => TreeFilterOperator.EndsWith,
                        "isnull" => TreeFilterOperator.IsNull,
                        _ => throw new GatewayInvalidQueryException($"Unsupported filter operator '{opName}' on '{fieldName}'.")
                    };

                        // #300: only the comparison operators take a null operand; list operators reject it with a fixed message.
                        if (opValLiteral is NullValueNode && filterOp is TreeFilterOperator.In or TreeFilterOperator.NotIn)
                        {
                            throw new GatewayInvalidQueryException($"Value for operator '{opName}' on field '{fieldName}' must be a list.");
                        }

                        object? typedValue = CoerceValue(rawVal, col.FieldType, filterOp, ColumnValueKinds.IsDateOnly(col.DataType));
                        items.Add(new TreeComparison(col.ColumnName, filterOp, typedValue)
                        {
                            BindNullOperand = opValLiteral is NullValueNode
                        });
                    }
                }
            }
        }

        if (items.Count == 0) return null;
        return items.Count == 1 ? items[0] : new TreeAndFilter(items);
    }

    private static IReadOnlyList<TreeOrder> ParseOrderByArgument(
        Selection selection,
        CatalogTableType table,
        IResolverContext context)
    {
        if (!selection.Arguments.TryGetValue("orderBy", out var orderArg) || orderArg?.ValueLiteral == null)
        {
            return [];
        }

        var literal = orderArg.ValueLiteral;
        if (literal is VariableNode varNode)
        {
            literal = ResolveVariableLiteral(varNode, context);
        }

        var orders = new List<TreeOrder>();

        if (literal is ListValueNode listNode)
        {
            foreach (var item in listNode.Items)
            {
                var itemLit = item is VariableNode vn ? ResolveVariableLiteral(vn, context) : item;
                if (itemLit is ObjectValueNode obj)
                {
                    orders.Add(ParseOrderEntry(obj, table, context));
                }
            }
        }
        else if (literal is ObjectValueNode singleObj)
        {
            orders.Add(ParseOrderEntry(singleObj, table, context));
        }

        return orders;
    }

    /// <summary>G-5: one orderBy entry; the direction may also be a variable ({ id: $dir }).</summary>
    private static TreeOrder ParseOrderEntry(ObjectValueNode entry, CatalogTableType table, IResolverContext context)
    {
        if (entry.Fields.Count != 1)
        {
            throw new GatewayInvalidQueryException("Each orderBy entry must specify exactly one field.");
        }

        var f = entry.Fields[0];
        var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, f.Name.Value, StringComparison.Ordinal))
            ?? throw new GatewayInvalidQueryException($"Unknown sort column '{f.Name.Value}'.");

        var dirLiteral = f.Value is VariableNode dirVar ? ResolveVariableLiteral(dirVar, context) : f.Value;
        var dirVal = dirLiteral switch
        {
            EnumValueNode ev => ev.Value,
            StringValueNode sv => sv.Value,
            _ => throw new GatewayInvalidQueryException($"Invalid sort direction for '{f.Name.Value}'. Expected ASC or DESC.")
        };
        if (!string.Equals(dirVal, "ASC", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dirVal, "DESC", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayInvalidQueryException($"Invalid sort direction '{dirVal}' for '{f.Name.Value}'. Expected ASC or DESC.");
        }

        return new TreeOrder(col.ColumnName, string.Equals(dirVal, "DESC", StringComparison.OrdinalIgnoreCase));
    }

    private static IValueNode ResolveVariableLiteral(VariableNode varNode, IResolverContext context)
    {
        var varName = varNode.Name.Value;
        if (context.Variables.TryGetValue<IValueNode>(varName, out var node) && node != null)
        {
            return node;
        }
        return NullValueNode.Default;
    }

    /// <summary>A page-size or offset argument: null/absent, a number, or not readable as an integer.</summary>
    private readonly record struct PageNumber(long? Value, bool Invalid);

    private static PageNumber ReadPageNumber(IValueNode node, IResolverContext context)
    {
        switch (node)
        {
            case NullValueNode:
                return default;
            case IntValueNode iv:
                return long.TryParse(iv.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    ? new PageNumber(parsed, false)
                    : new PageNumber(iv.Value.StartsWith('-') ? long.MinValue : long.MaxValue, false);
            case VariableNode vn:
                return ReadVariablePageNumber(vn.Name.Value, context);
            default:
                return new PageNumber(null, true);
        }
    }

    private static PageNumber ReadVariablePageNumber(string name, IResolverContext context)
    {
        var variables = context.Variables;
        if (variables.TryGetValue<IValueNode>(name, out var literal) && literal != null)
        {
            return literal is VariableNode ? default : ReadPageNumber(literal, context);
        }

        // Absent or null: the declared default of the argument applies (handled by the caller as "no value").
        return default;
    }

    private static object? ResolveValue(IValueNode node, IResolverContext context)
    {
        return node switch
        {
            VariableNode vn => ResolveValue(ResolveVariableLiteral(vn, context), context),
            IntValueNode iv => iv.ToInt64(),
            FloatValueNode fv => decimal.TryParse(fv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) ? (object)dec : fv.ToDouble(),
            StringValueNode sv => sv.Value,
            BooleanValueNode bv => bv.Value,
            EnumValueNode ev => ev.Value,
            NullValueNode => null,
            ListValueNode lv => lv.Items.Select(i => ResolveValue(i, context)).ToList(),
            ObjectValueNode ov => ov.Fields.ToDictionary(f => f.Name.Value, f => ResolveValue(f.Value, context)),
            _ => null
        };
    }

    internal static object? CoerceValue(object? rawVal, CatalogFieldType type, TreeFilterOperator op, bool dateOnly = false) =>
        FilterValueCoercion.Coerce(rawVal, ToKind(type), op, dateOnly);

    internal static object? CoerceSingleValue(object? val, CatalogFieldType type) =>
        FilterValueCoercion.CoerceSingle(val, ToKind(type));

    /// <summary>The Application value kind of a GraphQL field type (same members, same order).</summary>
    internal static ColumnValueKind ToKind(CatalogFieldType type) => type switch
    {
        CatalogFieldType.Int => ColumnValueKind.Int,
        CatalogFieldType.Long => ColumnValueKind.Long,
        CatalogFieldType.Float => ColumnValueKind.Float,
        CatalogFieldType.Decimal => ColumnValueKind.Decimal,
        CatalogFieldType.Boolean => ColumnValueKind.Boolean,
        CatalogFieldType.DateTime => ColumnValueKind.DateTime,
        _ => ColumnValueKind.String
    };
}
