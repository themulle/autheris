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

        var gqlOptions = context.Services.GetService(typeof(Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>)) as Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>;
        var maxBudget = gqlOptions?.Value?.GraphQL?.MaxAggregateRowBudget > 0
            ? gqlOptions.Value.GraphQL.MaxAggregateRowBudget
            : MaxAggregateBudget;
        var maxAllowedOffset = gqlOptions?.Value?.GraphQL?.MaxAllowedOffset >= 0
            ? gqlOptions.Value.GraphQL.MaxAllowedOffset
            : 10_000;

        var root = BuildNode(context, selection, operation, rootTable, schema, maxResponseRows, isRoot: true, maxAllowedOffset: maxAllowedOffset);
        ValidateTreeBudget(root, maxBudget);
        return root;
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
        long currentRows = isList ? parentMultiplier * node.Limit : parentMultiplier;
        long total = currentRows;
        foreach (var rel in node.Relations)
        {
            total += EstimateRows(rel.Child, currentRows, rel.IsList);
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
        bool isRoot,
        int maxAllowedOffset = 10_000)
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
            else if (int.TryParse(Convert.ToString(firstVal, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFirst)) limit = parsedFirst;
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
            else if (int.TryParse(Convert.ToString(offsetVal, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset)) offset = parsedOffset;
        }

        if (offset < 0)
        {
            throw new GatewayInvalidQueryException("The offset cannot be negative.");
        }

        if (offset > maxAllowedOffset)
        {
            throw new GatewayInvalidQueryException($"The offset cannot exceed {maxAllowedOffset}.");
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
                        // G-9: also isNull: null is rejected; it used to become IS NOT NULL.
                        if (opValLiteral is NullValueNode)
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

    private const NumberStyles StrictDecimalStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    internal static object? CoerceValue(object? rawVal, CatalogFieldType type, TreeFilterOperator op)
    {
        if (rawVal == null) return null;

        if (op == TreeFilterOperator.IsNull)
        {
            if (rawVal is bool b) return b;
            if (bool.TryParse(Convert.ToString(rawVal, CultureInfo.InvariantCulture), out var pb)) return pb;
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

    internal static object? CoerceSingleValue(object? val, CatalogFieldType type)
    {
        if (val == null) return null;

        switch (type)
        {
            case CatalogFieldType.Int:
                if (val is int iVal) return iVal;
                if (val is long lVal)
                {
                    if (lVal is < int.MinValue or > int.MaxValue)
                    {
                        throw new GatewayInvalidQueryException($"Integer value '{lVal}' is out of range.");
                    }
                    return (int)lVal;
                }
                if (val is short sVal) return (int)sVal;
                if (val is byte bVal) return (int)bVal;
                break;

            case CatalogFieldType.Long:
                if (val is long lVal2) return lVal2;
                if (val is int iVal2) return (long)iVal2;
                if (val is short sVal2) return (long)sVal2;
                if (val is byte bVal2) return (long)bVal2;
                break;

            case CatalogFieldType.Decimal:
                if (val is decimal decVal) return decVal;
                if (val is int iDec) return (decimal)iDec;
                if (val is long lDec) return (decimal)lDec;
                if (val is double dDec) return (decimal)dDec;
                if (val is float fDec) return (decimal)fDec;
                break;

            case CatalogFieldType.Float:
                if (val is double dVal) return dVal;
                if (val is float fVal) return (double)fVal;
                if (val is decimal decF) return (double)decF;
                if (val is int iF) return (double)iF;
                if (val is long lF) return (double)lF;
                break;

            case CatalogFieldType.Boolean:
                if (val is bool bVal3) return bVal3;
                break;

            case CatalogFieldType.DateTime:
                if (val is DateTimeOffset dtoVal) return dtoVal;
                // R-GQL-10: a DateTime without kind is UTC, not server local time.
                if (val is DateTime dtVal) return new DateTimeOffset(dtVal.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dtVal, DateTimeKind.Utc) : dtVal.ToUniversalTime(), TimeSpan.Zero);
                break;

            default:
                if (val is string sVal3) return sVal3;
                break;
        }

        var str = Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;

        return type switch
        {
            CatalogFieldType.Int => int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? (object)i
                : throw new GatewayInvalidQueryException($"Invalid integer value '{str}'."),
            CatalogFieldType.Long => long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? (object)l
                : throw new GatewayInvalidQueryException($"Invalid long integer value '{str}'."),
            CatalogFieldType.Decimal => decimal.TryParse(str, StrictDecimalStyles, CultureInfo.InvariantCulture, out var d)
                ? (object)d
                : throw new GatewayInvalidQueryException($"Invalid decimal value '{str}'."),
            CatalogFieldType.Float => double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? (object)f
                : throw new GatewayInvalidQueryException($"Invalid float value '{str}'."),
            CatalogFieldType.Boolean => bool.TryParse(str, out var pb)
                ? (object)pb
                : throw new GatewayInvalidQueryException($"Invalid boolean value '{str}'."),
            CatalogFieldType.DateTime => DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? (object)dto
                : throw new GatewayInvalidQueryException($"Invalid datetime value '{str}'."),
            _ => str
        };
    }
}
