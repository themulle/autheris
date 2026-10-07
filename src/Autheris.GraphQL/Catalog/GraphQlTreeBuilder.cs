using System.Globalization;
using System.Text.Json;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using HotChocolate.Resolvers;

namespace Autheris.GraphQL.Catalog;

public static class GraphQlTreeBuilder
{
    public static TreeQueryNode BuildTree(
        IResolverContext context,
        CatalogTableType rootTable,
        CatalogSchemaModel schema,
        int maxResponseRows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rootTable);
        ArgumentNullException.ThrowIfNull(schema);

        var selection = context.Selection;
        var operation = context.Operation;

        var root = BuildNode(context, selection, operation, rootTable, schema, maxResponseRows, isRoot: true);
        ValidateTreeBudget(root);
        return root;
    }

    public const int MaxAggregateBudget = 50_000;

    private static void ValidateTreeBudget(TreeQueryNode root)
    {
        long estimatedRows = EstimateRows(root, 1);
        if (estimatedRows > MaxAggregateBudget)
        {
            throw new GatewayInvalidQueryException($"The query exceeds the aggregate row budget of {MaxAggregateBudget} across nested relations (estimated worst-case rows: {estimatedRows}).");
        }
    }

    private static long EstimateRows(TreeQueryNode node, long parentMultiplier)
    {
        long currentRows = parentMultiplier * node.Limit;
        long total = currentRows;
        foreach (var rel in node.Relations)
        {
            total += EstimateRows(rel.Child, currentRows);
        }
        return total;
    }

    private static TreeQueryNode BuildNode(
        IResolverContext context,
        Selection currentSelection,
        Operation operation,
        CatalogTableType currentTable,
        CatalogSchemaModel schema,
        int maxAllowedLimit,
        bool isRoot)
    {
        // 1. Parse Arguments (where, orderBy, first, offset)
        var whereFilter = ParseWhereArgument(currentSelection, currentTable, context);
        var orderByList = ParseOrderByArgument(currentSelection, currentTable, context);

        int limit = isRoot ? Math.Min(100, maxAllowedLimit) : 100;
        int offset = 0;

        if (currentSelection.Arguments.TryGetValue("first", out var firstArg) && firstArg?.ValueLiteral != null)
        {
            var firstVal = ResolveValue(firstArg.ValueLiteral, context);
            if (firstVal is int fInt) limit = fInt;
            else if (firstVal is long fLng) limit = (int)fLng;
            else if (int.TryParse(firstVal?.ToString(), out var parsedFirst)) limit = parsedFirst;
        }

        if (limit < 1 || limit > maxAllowedLimit)
        {
            throw new GatewayInvalidQueryException($"The page size must be between 1 and {maxAllowedLimit}.");
        }

        if (currentSelection.Arguments.TryGetValue("offset", out var offsetArg) && offsetArg?.ValueLiteral != null)
        {
            var offsetVal = ResolveValue(offsetArg.ValueLiteral, context);
            if (offsetVal is int oInt) offset = oInt;
            else if (offsetVal is long oLng) offset = (int)oLng;
            else if (int.TryParse(offsetVal?.ToString(), out var parsedOffset)) offset = parsedOffset;
        }

        if (offset < 0)
        {
            throw new GatewayInvalidQueryException("The offset cannot be negative.");
        }

        const int MaxAllowedOffset = 10_000;
        if (offset > MaxAllowedOffset)
        {
            throw new GatewayInvalidQueryException($"The offset cannot exceed {MaxAllowedOffset}.");
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
                    isRoot: false);

                relations.Add(new TreeRelationNode(
                    ResponseKey: sel.ResponseName,
                    ParentColumns: rel.ParentColumns,
                    ChildColumns: rel.ChildColumns,
                    IsList: rel.IsList,
                    Child: childNode));
            }
        }

        return new TreeQueryNode(currentTable.Identifier, columns.ToList())
        {
            Relations = relations,
            Where = whereFilter,
            OrderBy = orderByList,
            Limit = limit,
            Offset = offset
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

    private static TreeFilter? ParseObjectFilter(
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
                        foreach (var item in listNode.Items)
                        {
                            var itemLiteral = item is VariableNode ivn ? ResolveVariableLiteral(ivn, context) : item;
                            if (itemLiteral is ObjectValueNode ovn)
                            {
                                var parsed = ParseObjectFilter(ovn, table, context);
                                if (parsed != null) orItems.Add(parsed);
                            }
                        }
                        if (orItems.Count > 0) items.Add(new TreeOrFilter(orItems));
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
                    var parsed = ParseObjectFilter(notObj, table, context);
                    if (parsed != null) items.Add(new TreeNotFilter(parsed));
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
                        if (opValLiteral is NullValueNode && opName != "isnull")
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

                        object? typedValue = CoerceValue(rawVal, col.FieldType, filterOp);
                        items.Add(new TreeComparison(col.ColumnName, filterOp, typedValue));
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
                    if (obj.Fields.Count != 1)
                    {
                        throw new GatewayInvalidQueryException("Each orderBy entry must specify exactly one field.");
                    }
                    var f = obj.Fields[0];
                    var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, f.Name.Value, StringComparison.Ordinal));
                    if (col == null)
                    {
                        throw new GatewayInvalidQueryException($"Unknown sort column '{f.Name.Value}'.");
                    }
                    var dirVal = f.Value switch
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
                    bool desc = string.Equals(dirVal, "DESC", StringComparison.OrdinalIgnoreCase);
                    orders.Add(new TreeOrder(col.ColumnName, desc));
                }
            }
        }
        else if (literal is ObjectValueNode singleObj)
        {
            if (singleObj.Fields.Count != 1)
            {
                throw new GatewayInvalidQueryException("Each orderBy entry must specify exactly one field.");
            }
            var f = singleObj.Fields[0];
            var col = table.Columns.FirstOrDefault(c => string.Equals(c.FieldName, f.Name.Value, StringComparison.Ordinal));
            if (col == null)
            {
                throw new GatewayInvalidQueryException($"Unknown sort column '{f.Name.Value}'.");
            }
            var dirVal = f.Value switch
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
            bool desc = string.Equals(dirVal, "DESC", StringComparison.OrdinalIgnoreCase);
            orders.Add(new TreeOrder(col.ColumnName, desc));
        }

        return orders;
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

    private static object? CoerceValue(object? rawVal, CatalogFieldType type, TreeFilterOperator op)
    {
        if (rawVal == null) return null;

        if (op == TreeFilterOperator.IsNull)
        {
            if (rawVal is bool b) return b;
            if (bool.TryParse(rawVal.ToString(), out var pb)) return pb;
            throw new GatewayInvalidQueryException("The 'isNull' filter value must be a boolean (true or false).");
        }

        if (op is TreeFilterOperator.In or TreeFilterOperator.NotIn)
        {
            if (rawVal is IEnumerable<object?> list)
            {
                return list.Select(item => CoerceSingleValue(item, type)).ToList();
            }
            return new List<object?> { CoerceSingleValue(rawVal, type) };
        }

        return CoerceSingleValue(rawVal, type);
    }

    private static object? CoerceSingleValue(object? val, CatalogFieldType type)
    {
        if (val == null) return null;
        var str = val.ToString()!;

        return type switch
        {
            CatalogFieldType.Int => int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? (object)i
                : throw new GatewayInvalidQueryException($"Invalid integer value '{str}'."),
            CatalogFieldType.Long => long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? (object)l
                : throw new GatewayInvalidQueryException($"Invalid long integer value '{str}'."),
            CatalogFieldType.Decimal => decimal.TryParse(str, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)
                ? (object)d
                : throw new GatewayInvalidQueryException($"Invalid decimal value '{str}'."),
            CatalogFieldType.Float => double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? (object)f
                : throw new GatewayInvalidQueryException($"Invalid float value '{str}'."),
            CatalogFieldType.Boolean => val is bool b ? b : (bool.TryParse(str, out var pb) ? pb : throw new GatewayInvalidQueryException($"Invalid boolean value '{str}'.")),
            CatalogFieldType.DateTime => DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto
                : throw new GatewayInvalidQueryException($"Invalid datetime value '{str}'."),
            _ => str
        };
    }
}
