namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using HotChocolate.Validation;

public readonly record struct FragmentCostKey(string Fragment, string? TypeName, int Depth);

public sealed class QueryCostAnalyzerRule : IDocumentValidatorRule
{
    private readonly int _maxAllowedCost;
    private readonly int _defaultListMultiplier;
    private readonly int _maxResponseRows;
    private readonly Action? _onQueryTooComplex;
    private readonly int _maxRootFields;
    private readonly int _payloadRowMultiplier;
    private readonly int _maxExecutionDepth;
    private readonly QueryCostOptions? _costOptions;
    private readonly Func<string, string, RelationCardinality?>? _relationLookup;
    private readonly double _aggregateFieldCostWeight;
    private readonly int _maxGroupByColumns;
    private readonly int _maxAggregates;
    private readonly int _maxAggregateFieldsPerOperation;

    // SEC M-13: Default-Seitengröße, die Resolver mit `first`-Argument (z.B. Query.GetTableAsync) ohne explizites Argument verwenden.
    internal const int DefaultPayloadPageSize = 50;

    public QueryCostAnalyzerRule(
        int maxAllowedCost = 250,
        int defaultListMultiplier = 10,
        int maxResponseRows = 1000,
        Action? onQueryTooComplex = null,
        int maxRootFields = 10,
        int payloadRowMultiplier = 1,
        int maxExecutionDepth = 25,
        QueryCostOptions? costOptions = null,
        Func<string, string, RelationCardinality?>? relationLookup = null,
        double aggregateFieldCostWeight = AggregationOptions.DefaultAggregateFieldCostWeight,
        int maxGroupByColumns = AggregationOptions.DefaultMaxGroupByColumns,
        int maxAggregates = AggregationOptions.DefaultMaxAggregates,
        int maxAggregateFieldsPerOperation = GraphQLAggregationOptions.DefaultMaxFieldsPerOperation)
    {
        _costOptions = costOptions;
        _relationLookup = relationLookup;
        _maxExecutionDepth = maxExecutionDepth > 0 ? maxExecutionDepth : 25;
        // #112 A-7: the three weights of the typed aggregate cost model (the same ones the engine's cost calculator uses).
        _aggregateFieldCostWeight = aggregateFieldCostWeight > 0 ? aggregateFieldCostWeight : AggregationOptions.DefaultAggregateFieldCostWeight;
        _maxGroupByColumns = maxGroupByColumns > 0 ? maxGroupByColumns : AggregationOptions.DefaultMaxGroupByColumns;
        _maxAggregates = maxAggregates > 0 ? maxAggregates : AggregationOptions.DefaultMaxAggregates;
        _maxAggregateFieldsPerOperation = maxAggregateFieldsPerOperation > 0 ? maxAggregateFieldsPerOperation : GraphQLAggregationOptions.DefaultMaxFieldsPerOperation;
        _maxAllowedCost = maxAllowedCost;
        _defaultListMultiplier = costOptions?.DefaultListMultiplier > 0 ? costOptions.DefaultListMultiplier : defaultListMultiplier;
        _maxResponseRows = maxResponseRows;
        _onQueryTooComplex = onQueryTooComplex;
        _maxRootFields = maxRootFields > 0 ? maxRootFields : int.MaxValue;
        _payloadRowMultiplier = Math.Max(1, payloadRowMultiplier);
    }

    public bool IsCacheable => true;
    public ushort Priority => 10;

    public static int ResolveMaxExecutionDepth(GraphQLOptions? options)
    {
        return options?.MaxAllowedExecutionDepth > 0 ? options.MaxAllowedExecutionDepth : 25;
    }

    public static int ResolveMaxExecutionDepth(GatewayOptions? options)
        => ResolveMaxExecutionDepth(options?.GraphQL);

    public static int CalculateCost(
        DocumentNode document,
        ISchemaDefinition schema,
        int defaultListMultiplier = 10,
        int maxResponseRows = 1000,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        QueryCostOptions? costOptions = null,
        Func<string, string, RelationCardinality?>? relationLookup = null,
        AggregationOptions? aggregation = null,
        int maxExecutionDepth = 25)
    {
        var rule = new QueryCostAnalyzerRule(
            int.MaxValue,
            defaultListMultiplier,
            maxResponseRows,
            maxExecutionDepth: maxExecutionDepth,
            costOptions: costOptions,
            relationLookup: relationLookup,
            aggregateFieldCostWeight: aggregation?.AggregateFieldCostWeight ?? AggregationOptions.DefaultAggregateFieldCostWeight,
            maxGroupByColumns: aggregation?.MaxGroupByColumns ?? AggregationOptions.DefaultMaxGroupByColumns,
            maxAggregates: aggregation?.MaxAggregates ?? AggregationOptions.DefaultMaxAggregates);
        return rule.ComputeCost(document, schema, variableValues);
    }

    public static int CalculateCost(
        DocumentNode document,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, object?>? variableValues)
    {
        return CalculateCost(document, schema, 10, 1000, variableValues);
    }

    public static (int TotalCost, IReadOnlyDictionary<string, int> RootFieldCosts) CalculateCostDetails(
        DocumentNode document,
        ISchemaDefinition schema,
        int defaultListMultiplier = 10,
        int maxResponseRows = 1000,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        QueryCostOptions? costOptions = null,
        Func<string, string, RelationCardinality?>? relationLookup = null,
        int maxExecutionDepth = 25)
    {
        var rule = new QueryCostAnalyzerRule(
            int.MaxValue,
            defaultListMultiplier,
            maxResponseRows,
            maxExecutionDepth: maxExecutionDepth,
            costOptions: costOptions,
            relationLookup: relationLookup);
        return rule.ComputeCostDetails(document, schema, variableValues);
    }

    public static (int TotalCost, IReadOnlyDictionary<string, int> RootFieldCosts) CalculateCostDetails(
        DocumentNode document,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, object?>? variableValues)
    {
        return CalculateCostDetails(document, schema, 10, 1000, variableValues);
    }

    public void Validate(DocumentValidatorContext context, DocumentNode document)
    {
        // SEC M-13: Harte Obergrenze für Root-Felder/Aliase pro Operation (Alias-Amplifikation).
        int maxRootFieldCount = CountMaxRootFields(document, _maxRootFields);
        if (maxRootFieldCount > _maxRootFields)
        {
            _onQueryTooComplex?.Invoke();
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage($"The query exceeds the maximum of {_maxRootFields} root fields or aliases per operation.")
                    .SetCode("QUERY_TOO_COMPLEX")
                    .SetExtension("maxRootFields", _maxRootFields)
                    .Build());
            return;
        }

        // #112 SEC-112-19: every aliased aggregate or metric field counts; decided before any resolver runs, text without schema details.
        if (CountAggregateRootFields(document, context.Schema, _maxAggregateFieldsPerOperation) > _maxAggregateFieldsPerOperation)
        {
            _onQueryTooComplex?.Invoke();
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("The query contains too many aggregation fields.")
                    .SetCode("QUERY_TOO_COMPLEX")
                    .SetExtension("maxAggregateFields", _maxAggregateFieldsPerOperation)
                    .Build());
            return;
        }

        int totalCost = ComputeCost(document, context.Schema);
        if (totalCost > _maxAllowedCost)
        {
            _onQueryTooComplex?.Invoke();
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage($"The query exceeds the complexity budget of {_maxAllowedCost} (calculated cost: {totalCost}). Nested list fields require 'first' (e.g. first: 10) or lower pagination limits to reduce query cost within budget. If using variables for pagination limits, declare a default value (e.g. $first: Int = 20).")
                    .SetCode("QUERY_TOO_COMPLEX")
                    .SetExtension("calculatedCost", totalCost)
                    .SetExtension("maxAllowedCost", _maxAllowedCost)
                    .SetExtension("hint", "Add 'first: <n>' to nested list selections to bound query cost within budget.")
                    .Build());
        }
    }

    /// <summary>
    /// SEC M-13: Liefert die größte Anzahl an Root-Feldern (inkl. Aliase, aufgelöst über Inline-Fragmente und
    /// Fragment-Spreads) über alle Operationen des Dokuments. Bricht ab, sobald <paramref name="stopAfter"/> überschritten ist.
    /// </summary>
    public static int CountMaxRootFields(DocumentNode document, int stopAfter = int.MaxValue)
    {
        var fragments = new Dictionary<string, FragmentDefinitionNode>(StringComparer.Ordinal);
        foreach (var fragment in document.Definitions.OfType<FragmentDefinitionNode>())
        {
            fragments.TryAdd(fragment.Name.Value, fragment);
        }

        int max = 0;
        foreach (var operation in document.Definitions.OfType<OperationDefinitionNode>())
        {
            int count = 0;
            CountRootFields(operation.SelectionSet, fragments, new HashSet<string>(StringComparer.Ordinal), ref count, stopAfter);
            max = Math.Max(max, count);
            if (max > stopAfter)
            {
                break;
            }
        }

        return max;
    }

    private static void CountRootFields(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        ref int count,
        int stopAfter)
    {
        foreach (var selection in selectionSet.Selections)
        {
            if (count > stopAfter)
            {
                return;
            }

            switch (selection)
            {
                case FieldNode field:
                    if (!field.Name.Value.StartsWith("__", StringComparison.Ordinal))
                    {
                        count++;
                    }
                    break;
                case InlineFragmentNode inline:
                    CountRootFields(inline.SelectionSet, fragments, activeFragments, ref count, stopAfter);
                    break;
                case FragmentSpreadNode spread:
                    if (fragments.TryGetValue(spread.Name.Value, out var fragDef) && activeFragments.Add(fragDef.Name.Value))
                    {
                        CountRootFields(fragDef.SelectionSet, fragments, activeFragments, ref count, stopAfter);
                        activeFragments.Remove(fragDef.Name.Value);
                    }
                    break;
            }
        }
    }

    private static bool IsRowLimitArgument(string name) =>
        string.Equals(name, "first", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "last", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "limit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "take", StringComparison.OrdinalIgnoreCase);

    private static int SafeAdd(int a, int b) => (int)Math.Min((long)int.MaxValue, (long)a + b);

    public (int TotalCost, IReadOnlyDictionary<string, int> RootFieldCosts) ComputeCostDetails(
        DocumentNode document,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, object?>? variableValues = null)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        var activeFragments = new HashSet<string>(StringComparer.Ordinal);
        var fragmentCostCache = new Dictionary<FragmentCostKey, int>();
        var maskingCostCache = new Dictionary<FragmentCostKey, int>();
        int spreadCounter = 0;
        const int maxSpreadExpansions = 250;
        int totalCost = 0;
        var rootFieldCosts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var def in document.Definitions)
        {
            if (def is OperationDefinitionNode operation)
            {
                var rootType = operation.Operation switch
                {
                    OperationType.Mutation => schema.MutationType,
                    OperationType.Subscription => schema.SubscriptionType,
                    _ => schema.QueryType
                };

                foreach (var selection in operation.SelectionSet.Selections)
                {
                    string key;
                    if (selection is FieldNode field)
                    {
                        key = field.Alias?.Value ?? field.Name.Value;
                    }
                    else if (selection is InlineFragmentNode inline)
                    {
                        key = inline.TypeCondition != null ? $"...on {inline.TypeCondition.Name.Value}" : "...inline";
                    }
                    else if (selection is FragmentSpreadNode spread)
                    {
                        key = $"...{spread.Name.Value}";
                    }
                    else
                    {
                        key = selection.ToString() ?? "unknown";
                    }

                    var singleSelection = new SelectionSetNode(null, [selection]);
                    int selectionCost = CalculateSelectionSetCost(
                        singleSelection,
                        rootType,
                        fragments,
                        activeFragments,
                        fragmentCostCache,
                        maskingCostCache,
                        schema,
                        ref spreadCounter,
                        maxSpreadExpansions,
                        operation.VariableDefinitions,
                        variableValues,
                        currentDepth: 1);

                    rootFieldCosts[key] = rootFieldCosts.TryGetValue(key, out var existing)
                        ? SafeAdd(existing, selectionCost)
                        : selectionCost;

                    totalCost = SafeAdd(totalCost, selectionCost);
                }
            }
        }

        return (Math.Max(1, totalCost), rootFieldCosts);
    }

    public int ComputeCost(
        DocumentNode document,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, object?>? variableValues = null)
    {
        return ComputeCostDetails(document, schema, variableValues).TotalCost;
    }

    private int CalculateSelectionSetCost(
        SelectionSetNode? selectionSet,
        IObjectTypeDefinition? currentType,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<FragmentCostKey, int> fragmentCostCache,
        Dictionary<FragmentCostKey, int> maskingCostCache,
        ISchemaDefinition schema,
        ref int spreadCounter,
        int maxSpreadExpansions,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions = null,
        IReadOnlyDictionary<string, object?>? variableValues = null,
        int currentDepth = 1)
    {
        if (selectionSet == null || selectionSet.Selections.Count == 0)
        {
            return 0;
        }

        if (currentDepth > _maxExecutionDepth)
        {
            return SafeAdd(_maxAllowedCost, 1000);
        }

        int cost = 0;
        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode field)
            {
                if (field.Name.Value.StartsWith("__", StringComparison.Ordinal))
                {
                    cost = SafeAdd(cost, 1);
                    continue;
                }

                IOutputFieldDefinition? fieldDef = null;
                currentType?.Fields.TryGetField(field.Name.Value, out fieldDef);

                // #112 A-7: a typed aggregate field costs by its groupBy and aggregates, not by its row limit; the selection below it
                // (groups, key, aggregates) is not multiplied again because the field cost already models the whole result.
                if (fieldDef != null && IsTypedAggregateField(field.Name.Value, fieldDef))
                {
                    cost = SafeAdd(cost, AggregateFieldCost(field, variableDefinitions, variableValues));
                    continue;
                }

                bool isList = false;
                IObjectTypeDefinition? nextType = null;

                if (fieldDef != null)
                {
                    isList = IsListType(fieldDef.Type);
                    var named = UnwrapType(fieldDef.Type);
                    if (named is IObjectTypeDefinition ot)
                    {
                        nextType = ot;
                    }
                    else if (named is INameProvider np && schema.Types.TryGetType<IObjectTypeDefinition>(np.Name, out var foundOt))
                    {
                        nextType = foundOt;
                    }
                }
                else
                {
                    isList = field.Arguments.Any(a => IsRowLimitArgument(a.Name.Value));
                }

                // SEC M-13: Pagination wird unabhängig vom Rückgabetyp erkannt (auch Objekt-Payloads wie TableRecordPayload).
                bool acceptsPagination = fieldDef != null
                    ? fieldDef.Arguments.Any(a => IsRowLimitArgument(a.Name))
                    : field.Arguments.Any(a => IsRowLimitArgument(a.Name.Value));

                if (acceptsPagination)
                {
                    int requestedLimit = -1;
                    bool limitIsVariable = false;
                    bool zeroLimit = false;
                    foreach (var arg in field.Arguments)
                    {
                        if (IsRowLimitArgument(arg.Name.Value))
                        {
                            if (arg.Value is IntValueNode intVal && int.TryParse(intVal.Value, out var parsed))
                            {
                                requestedLimit = parsed;
                                zeroLimit = parsed == 0;
                                break;
                            }

                            if (arg.Value is VariableNode varNode)
                            {
                                limitIsVariable = true;
                                if (variableValues != null && variableValues.TryGetValue(varNode.Name.Value, out var val) && TryExtractPositiveInt(val, out var runtimeLimit))
                                {
                                    requestedLimit = runtimeLimit;
                                    break;
                                }

                                // SEC-PG-07: a runtime value of 0 is a bounded query, never the worst case and never free.
                                if (variableValues != null && variableValues.TryGetValue(varNode.Name.Value, out var zeroVal) && IsZeroLimit(zeroVal))
                                {
                                    zeroLimit = true;
                                    break;
                                }

                                if (variableDefinitions != null)
                                {
                                    var varDef = variableDefinitions.FirstOrDefault(v => string.Equals(v.Variable.Name.Value, varNode.Name.Value, StringComparison.Ordinal));
                                    if (varDef?.DefaultValue is IntValueNode defInt && int.TryParse(defInt.Value, out var defParsed) && defParsed > 0)
                                    {
                                        requestedLimit = defParsed;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    int effectiveRows;
                    if (zeroLimit)
                    {
                        // SEC-PG-07 (#302): first: 0 is priced as the minimum bounded query (one row), never as zero cost or unbounded.
                        effectiveRows = 1;
                    }
                    else if (requestedLimit > 0)
                    {
                        effectiveRows = Math.Min(requestedLimit, _maxResponseRows);
                    }
                    else if (string.Equals(field.Name.Value, "catalog", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveRows = 10;
                    }
                    else if (!isList && !limitIsVariable)
                    {
                        effectiveRows = Math.Min(DefaultPayloadPageSize, _maxResponseRows);
                    }
                    else
                    {
                        // Variablen oder fehlende Limits an Listen: Worst-Case annehmen.
                        effectiveRows = _maxResponseRows;
                    }

                    int multiplier = (isList && effectiveRows > 1) ? _defaultListMultiplier : _payloadRowMultiplier;
                    cost = SafeAdd(cost, (int)Math.Min((long)int.MaxValue, (long)multiplier * effectiveRows));

                    if (field.SelectionSet != null)
                    {
                        // SEC M-9: Multiply nested list selection costs by effectiveRows using saturating arithmetic
                        var maskingCost = CalculateMaskingCost(field.SelectionSet, fragments, activeFragments, maskingCostCache, currentDepth + 1);
                        var childCost = CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions, variableDefinitions, variableValues, currentDepth + 1);
                        var totalChild = SafeAdd(maskingCost, childCost);
                        if (isList)
                        {
                            var nestedCost = (int)Math.Min((long)int.MaxValue, (long)totalChild * Math.Max(1, effectiveRows));
                            cost = SafeAdd(cost, nestedCost);
                        }
                        else
                        {
                            cost = SafeAdd(cost, totalChild);
                        }
                    }
                }
                else
                {
                    // Non-paginated entity, scalar, or unpaginated relation list
                    if (field.SelectionSet != null)
                    {
                        var childCost = CalculateSelectionSetCost(
                            field.SelectionSet,
                            nextType,
                            fragments,
                            activeFragments,
                            fragmentCostCache,
                            maskingCostCache,
                            schema,
                            ref spreadCounter,
                            maxSpreadExpansions,
                            variableDefinitions,
                            variableValues,
                            currentDepth + 1);

                        if (isList)
                        {
                            int assumedRows = Math.Min(Math.Max(1, _defaultListMultiplier), _maxResponseRows);
                            cost = SafeAdd(cost, assumedRows);
                            var nestedCost = (int)Math.Min((long)int.MaxValue, (long)childCost * assumedRows);
                            cost = SafeAdd(cost, nestedCost);
                        }
                        else
                        {
                            // SEC-GOV177-05: Single object selection (1:1 / N:1 vs uncataloged/ambiguous)
                            bool isRootField = currentDepth <= 1 ||
                                               (currentType != null && (currentType == schema.QueryType ||
                                                                        currentType == schema.MutationType ||
                                                                        currentType == schema.SubscriptionType));
                            bool isVerifiedSingle = isRootField || IsVerifiedSingleObjectRelation(currentType, field.Name.Value, fieldDef);
                            int rowMultiplier = isVerifiedSingle
                                ? (_costOptions?.CostPerSingleRowLookup ?? 1)
                                : _defaultListMultiplier;

                            cost = SafeAdd(cost, rowMultiplier);
                            var nestedCost = (int)Math.Min((long)int.MaxValue, (long)childCost * rowMultiplier);
                            cost = SafeAdd(cost, nestedCost);
                        }
                    }
                    else
                    {
                        // Scalar field
                        int assumedRows = Math.Min(Math.Max(1, _defaultListMultiplier), _maxResponseRows);
                        cost = SafeAdd(cost, isList ? assumedRows : 1);
                    }
                }
            }
            else if (selection is InlineFragmentNode inlineFrag)
            {
                IObjectTypeDefinition? inlineType = currentType;
                if (inlineFrag.TypeCondition != null && schema.Types.TryGetType<IObjectTypeDefinition>(inlineFrag.TypeCondition.Name.Value, out var foundType))
                {
                    inlineType = foundType;
                }

                cost = SafeAdd(cost, CalculateSelectionSetCost(inlineFrag.SelectionSet, inlineType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions, variableDefinitions, variableValues, currentDepth));
            }
            else if (selection is FragmentSpreadNode fragmentSpread)
            {
                spreadCounter++;
                if (spreadCounter > maxSpreadExpansions)
                {
                    return SafeAdd(_maxAllowedCost, 1000);
                }

                IObjectTypeDefinition? fragType = currentType;
                if (fragments.TryGetValue(fragmentSpread.Name.Value, out var fragDef) && fragDef.TypeCondition != null && schema.Types.TryGetType<IObjectTypeDefinition>(fragDef.TypeCondition.Name.Value, out var foundType))
                {
                    fragType = foundType;
                }

                var cacheKey = new FragmentCostKey(fragmentSpread.Name.Value, fragType?.Name, currentDepth);

                if (fragmentCostCache.TryGetValue(cacheKey, out var cachedFragCost))
                {
                    cost = SafeAdd(cost, cachedFragCost);
                }
                else if (fragments.TryGetValue(fragmentSpread.Name.Value, out fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    int fragCost = CalculateSelectionSetCost(fragDef.SelectionSet, fragType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions, variableDefinitions, variableValues, currentDepth);
                    activeFragments.Remove(fragDef.Name.Value);
                    fragmentCostCache[cacheKey] = fragCost;
                    cost = SafeAdd(cost, fragCost);
                }
            }
        }

        return cost;
    }

    /// <summary>
    /// #112 SEC-112-19: the largest number of typed aggregate and <c>queryMetrics</c> root fields (aliases counted separately, fragments
    /// resolved) over the operations of the document. Stops once <paramref name="stopAfter"/> is exceeded.
    /// </summary>
    public static int CountAggregateRootFields(DocumentNode document, ISchemaDefinition schema, int stopAfter = int.MaxValue)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .GroupBy(f => f.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var max = 0;
        foreach (var operation in document.Definitions.OfType<OperationDefinitionNode>())
        {
            if (operation.Operation != OperationType.Query)
            {
                continue;
            }

            var count = 0;
            CountAggregateFields(operation.SelectionSet, schema.QueryType, schema, fragments, new HashSet<string>(StringComparer.Ordinal), ref count, stopAfter);
            max = Math.Max(max, count);
            if (max > stopAfter)
            {
                break;
            }
        }

        return max;
    }

    private static void CountAggregateFields(
        SelectionSetNode selectionSet,
        IObjectTypeDefinition rootType,
        ISchemaDefinition schema,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        ref int count,
        int stopAfter)
    {
        foreach (var selection in selectionSet.Selections)
        {
            if (count > stopAfter)
            {
                return;
            }

            switch (selection)
            {
                case FieldNode field:
                    if (rootType.Fields.TryGetField(field.Name.Value, out var def) && IsTypedAggregateField(field.Name.Value, def))
                    {
                        count++;
                    }

                    break;
                case InlineFragmentNode inline:
                    CountAggregateFields(inline.SelectionSet, rootType, schema, fragments, activeFragments, ref count, stopAfter);
                    break;
                case FragmentSpreadNode spread:
                    if (fragments.TryGetValue(spread.Name.Value, out var fragment) && activeFragments.Add(fragment.Name.Value))
                    {
                        CountAggregateFields(fragment.SelectionSet, rootType, schema, fragments, activeFragments, ref count, stopAfter);
                        activeFragments.Remove(fragment.Name.Value);
                    }

                    break;
            }
        }
    }

    /// <summary>A generated <c>T_aggregate</c> field (name and result type suffix) or the shared <c>queryMetrics</c> field.</summary>
    private static bool IsTypedAggregateField(string fieldName, IOutputFieldDefinition definition)
    {
        if (string.Equals(fieldName, "queryMetrics", StringComparison.Ordinal))
        {
            return true;
        }

        return fieldName.EndsWith("_aggregate", StringComparison.Ordinal) &&
               UnwrapType(definition.Type) is INameProvider { Name: var typeName } &&
               typeName.EndsWith("_aggregate_result", StringComparison.Ordinal);
    }

    private int AggregateFieldCost(
        FieldNode field,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions,
        IReadOnlyDictionary<string, object?>? variableValues)
    {
        // queryMetrics has no typed groupBy/aggregates list: it keeps the generic cost of a payload field via the base weight only.
        double cost = _aggregateFieldCostWeight;

        var groupBy = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "groupBy", StringComparison.Ordinal));
        cost += (groupBy is null ? 0 : (TryListItems(groupBy.Value, variableDefinitions, variableValues, out var groupItems) ? groupItems.Count : _maxGroupByColumns))
                * AggregationCostCalculator.GroupByColumnWeight;

        var aggregates = field.Arguments.FirstOrDefault(a => string.Equals(a.Name.Value, "aggregates", StringComparison.Ordinal));
        if (aggregates is null)
        {
            if (!string.Equals(field.Name.Value, "queryMetrics", StringComparison.Ordinal))
            {
                cost += AggregationCostCalculator.AggregateSelectionWeight;
            }
        }
        else if (TryListItems(aggregates.Value, variableDefinitions, variableValues, out var items))
        {
            foreach (var item in items)
            {
                cost += IsCountDistinct(item, variableValues)
                    ? AggregationCostCalculator.CountDistinctWeight
                    : AggregationCostCalculator.AggregateSelectionWeight;
            }
        }
        else
        {
            cost += _maxAggregates * AggregationCostCalculator.CountDistinctWeight;
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(cost));
    }

    private static bool TryListItems(
        IValueNode? value,
        IReadOnlyList<VariableDefinitionNode>? variableDefinitions,
        IReadOnlyDictionary<string, object?>? variableValues,
        out IReadOnlyList<object?> items)
    {
        items = [];
        switch (value)
        {
            case null or NullValueNode:
                return true;
            case ListValueNode list:
                items = list.Items.Cast<object?>().ToList();
                return true;
            case VariableNode variable:
                if (variableValues != null && variableValues.TryGetValue(variable.Name.Value, out var runtime))
                {
                    return TryRuntimeItems(runtime, out items);
                }

                var definition = variableDefinitions?.FirstOrDefault(v => string.Equals(v.Variable.Name.Value, variable.Name.Value, StringComparison.Ordinal));
                if (definition?.DefaultValue is { } defaultValue and not VariableNode)
                {
                    return TryListItems(defaultValue, variableDefinitions, variableValues, out items);
                }

                return false;
            default:
                // GraphQL list coercion: a single value stands for a one-element list.
                items = [value];
                return true;
        }
    }

    private static bool TryRuntimeItems(object? runtime, out IReadOnlyList<object?> items)
    {
        items = [];
        switch (runtime)
        {
            case null or NullValueNode:
                return true;
            case ListValueNode list:
                items = list.Items.Cast<object?>().ToList();
                return true;
            case IValueNode single:
                items = [single];
                return true;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } json:
                items = json.EnumerateArray().Cast<object?>().ToList();
                return true;
            case string:
                return false;
            case System.Collections.IEnumerable enumerable:
                items = enumerable.Cast<object?>().ToList();
                return true;
            default:
                return false;
        }
    }

    private static bool IsCountDistinct(object? item, IReadOnlyDictionary<string, object?>? variableValues)
    {
        switch (item)
        {
            case ObjectValueNode obj:
                var fn = obj.Fields.FirstOrDefault(f => string.Equals(f.Name.Value, "fn", StringComparison.Ordinal))?.Value;
                return FunctionIsCountDistinct(fn, variableValues);
            case VariableNode variable when variableValues != null && variableValues.TryGetValue(variable.Name.Value, out var runtime):
                return IsCountDistinct(runtime, variableValues);
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } json:
                return !json.TryGetProperty("fn", out var jsonFn) || IsDistinctName(jsonFn.GetString());
            case System.Collections.IDictionary dictionary:
                return IsDistinctName(dictionary.Contains("fn") ? dictionary["fn"]?.ToString() : null);
            case IReadOnlyDictionary<string, object?> readOnly:
                return IsDistinctName(readOnly.TryGetValue("fn", out var raw) ? raw?.ToString() : null);
            default:
                // Unresolvable: the worst case.
                return true;
        }
    }

    private static bool FunctionIsCountDistinct(IValueNode? fn, IReadOnlyDictionary<string, object?>? variableValues) => fn switch
    {
        EnumValueNode e => IsDistinctName(e.Value),
        StringValueNode s => IsDistinctName(s.Value),
        VariableNode v when variableValues != null && variableValues.TryGetValue(v.Name.Value, out var runtime) =>
            runtime switch
            {
                EnumValueNode e => IsDistinctName(e.Value),
                StringValueNode s => IsDistinctName(s.Value),
                string text => IsDistinctName(text),
                _ => true
            },
        _ => true
    };

    /// <summary>Only the exact names of the cheap functions are cheap; anything else (including a missing name) is priced as COUNT_DISTINCT.</summary>
    private static bool IsDistinctName(string? name) =>
        name is not ("COUNT" or "SUM" or "AVG" or "MIN" or "MAX");

    private static bool IsZeroLimit(object? val) => val switch
    {
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        byte b => b == 0,
        System.Text.Json.JsonElement je => je.ValueKind == System.Text.Json.JsonValueKind.Number
            ? je.TryGetInt32(out var ji) && ji == 0
            : je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var js) && js == 0,
        IntValueNode ivn => int.TryParse(ivn.Value, out var iv) && iv == 0,
        _ => false
    };

    private static bool TryExtractPositiveInt(object? val, out int result)
    {
        result = 0;
        if (val == null) return false;
        if (val is int i) { result = i; return i > 0; }
        if (val is long l && l <= int.MaxValue && l > 0) { result = (int)l; return true; }
        if (val is short s && s > 0) { result = s; return true; }
        if (val is byte b && b > 0) { result = b; return true; }
        if (val is IntValueNode ivn && int.TryParse(ivn.Value, out var ivnParsed) && ivnParsed > 0) { result = ivnParsed; return true; }
        if (val is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var ji) && ji > 0)
            {
                result = ji;
                return true;
            }
            if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var js) && js > 0)
            {
                result = js;
                return true;
            }
        }
        if (int.TryParse(val.ToString(), out var parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }
        return false;
    }

    private int CalculateMaskingCost(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<FragmentCostKey, int> maskingCostCache,
        int currentDepth = 1)
    {
        if (selectionSet == null || selectionSet.Selections.Count == 0)
        {
            return 0;
        }

        if (currentDepth > _maxExecutionDepth)
        {
            return SafeAdd(_maxAllowedCost, 1000);
        }

        int maskingCost = 0;
        foreach (var childSel in selectionSet.Selections)
        {
            if (childSel is FieldNode childField)
            {
                if (IsMaskedCandidate(childField.Name.Value))
                {
                    maskingCost = SafeAdd(maskingCost, 3);
                }
            }
            else if (childSel is InlineFragmentNode inlineFrag)
            {
                maskingCost = SafeAdd(maskingCost, CalculateMaskingCost(inlineFrag.SelectionSet, fragments, activeFragments, maskingCostCache, currentDepth));
            }
            else if (childSel is FragmentSpreadNode spread)
            {
                string? typeName = null;
                if (fragments.TryGetValue(spread.Name.Value, out var fragDef) && fragDef.TypeCondition != null)
                {
                    typeName = fragDef.TypeCondition.Name.Value;
                }

                var cacheKey = new FragmentCostKey(spread.Name.Value, typeName, currentDepth);

                if (maskingCostCache.TryGetValue(cacheKey, out var cachedMaskCost))
                {
                    maskingCost = SafeAdd(maskingCost, cachedMaskCost);
                }
                else if (fragments.TryGetValue(spread.Name.Value, out fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    int fragMaskCost = CalculateMaskingCost(fragDef.SelectionSet, fragments, activeFragments, maskingCostCache, currentDepth);
                    activeFragments.Remove(fragDef.Name.Value);
                    maskingCostCache[cacheKey] = fragMaskCost;
                    maskingCost = SafeAdd(maskingCost, fragMaskCost);
                }
            }
        }
        return maskingCost;
    }

    private static bool IsMaskedCandidate(string fieldName)
    {
        return fieldName.Contains("email", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("iban", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("salary", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("creditcard", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("mask", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsListType(IType type)
    {
        var current = type;
        while (current is IWrapperType wrapper)
        {
            if (current.Kind == TypeKind.List) return true;
            current = wrapper.InnerType;
        }
        return current.Kind == TypeKind.List;
    }

    private static IType UnwrapType(IType type)
    {
        var current = type;
        while (current is IWrapperType wrapper)
        {
            current = wrapper.InnerType;
        }
        return current;
    }

    private bool IsVerifiedSingleObjectRelation(
        IObjectTypeDefinition? currentType,
        string fieldName,
        IOutputFieldDefinition? fieldDef)
    {
        // SEC-GOV177-05: Multiplier 1.0 is ONLY permitted when the schema field and catalog metadata
        // explicitly verify RelationCardinality.OneToOne or RelationCardinality.ManyToOne and IsListType == false.
        // Ambiguous, uncataloged, union, interface, or unverified field types MUST fail closed and apply DefaultListMultiplier (10).

        if (fieldDef != null && IsListType(fieldDef.Type))
        {
            return false;
        }

        string? typeName = currentType?.Name;

        // 1. Direct relation lookup delegate if provided
        if (_relationLookup != null && !string.IsNullOrEmpty(typeName))
        {
            var card = _relationLookup(typeName, fieldName);
            if (card is RelationCardinality.OneToOne or RelationCardinality.ManyToOne)
            {
                return true;
            }
        }

        // 2. CatalogRelationRegistry check
        if (!string.IsNullOrEmpty(typeName) && CatalogRelationRegistry.TryGetCardinality(typeName, fieldName, out var registryCard))
        {
            if (registryCard is RelationCardinality.OneToOne or RelationCardinality.ManyToOne)
            {
                return true;
            }
        }

        // 3. Schema directive check on field definition
        if (fieldDef is IDirectivesProvider dirProvider)
        {
            var dir = dirProvider.Directives.FirstOrDefault("relation") ??
                      dirProvider.Directives.FirstOrDefault("cardinality");
            if (dir != null)
            {
                if (dir.Arguments.TryGetValue("cardinality", out var cVal) && cVal is StringValueNode sn)
                {
                    if (Enum.TryParse<RelationCardinality>(sn.Value, true, out var parsedCard) &&
                        parsedCard is RelationCardinality.OneToOne or RelationCardinality.ManyToOne)
                    {
                        return true;
                    }
                }
                if (dir.Arguments.TryGetValue("value", out var vVal) && vVal is StringValueNode svn)
                {
                    if (Enum.TryParse<RelationCardinality>(svn.Value, true, out var parsedCard) &&
                        parsedCard is RelationCardinality.OneToOne or RelationCardinality.ManyToOne)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
