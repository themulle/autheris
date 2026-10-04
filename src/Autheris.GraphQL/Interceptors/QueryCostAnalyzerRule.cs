namespace Autheris.GraphQL.Interceptors;

using System;
using System.Linq;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using HotChocolate.Validation;

public sealed class QueryCostAnalyzerRule : IDocumentValidatorRule
{
    private readonly int _maxAllowedCost;
    private readonly int _defaultListMultiplier;
    private readonly int _maxResponseRows;
    private readonly Action? _onQueryTooComplex;
    private readonly int _maxRootFields;
    private readonly int _payloadRowMultiplier;

    // SEC M-13: Default-Seitengröße, die Resolver mit `first`-Argument (z.B. Query.GetTableAsync) ohne explizites Argument verwenden.
    internal const int DefaultPayloadPageSize = 50;

    public QueryCostAnalyzerRule(
        int maxAllowedCost = 250,
        int defaultListMultiplier = 10,
        int maxResponseRows = 1000,
        Action? onQueryTooComplex = null,
        int maxRootFields = 10,
        int payloadRowMultiplier = 1)
    {
        _maxAllowedCost = maxAllowedCost;
        _defaultListMultiplier = defaultListMultiplier;
        _maxResponseRows = maxResponseRows;
        _onQueryTooComplex = onQueryTooComplex;
        _maxRootFields = maxRootFields > 0 ? maxRootFields : int.MaxValue;
        _payloadRowMultiplier = Math.Max(1, payloadRowMultiplier);
    }

    public bool IsCacheable => true;
    public ushort Priority => 10;

    public static int CalculateCost(DocumentNode document, ISchemaDefinition schema, int defaultListMultiplier = 10, int maxResponseRows = 1000)
    {
        var rule = new QueryCostAnalyzerRule(int.MaxValue, defaultListMultiplier, maxResponseRows);
        return rule.ComputeCost(document, schema);
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
                    .SetMessage($"Die Abfrage überschreitet die maximale Anzahl von {_maxRootFields} Root-Feldern bzw. Aliasen pro Operation.")
                    .SetCode("QUERY_TOO_COMPLEX")
                    .SetExtension("maxRootFields", _maxRootFields)
                    .Build());
            return;
        }

        int totalCost = ComputeCost(document, context.Schema);
        if (totalCost > _maxAllowedCost)
        {
            _onQueryTooComplex?.Invoke();
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage($"Die Abfrage überschreitet das Komplexitätsbudget von {_maxAllowedCost} (berechnete Kosten: {totalCost}).")
                    .SetCode("QUERY_TOO_COMPLEX")
                    .SetExtension("calculatedCost", totalCost)
                    .SetExtension("maxAllowedCost", _maxAllowedCost)
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

    public int ComputeCost(DocumentNode document, ISchemaDefinition schema)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        var activeFragments = new HashSet<string>(StringComparer.Ordinal);
        var fragmentCostCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var maskingCostCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int spreadCounter = 0;
        const int maxSpreadExpansions = 250;
        int totalCost = 0;

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

                totalCost = SafeAdd(totalCost, CalculateSelectionSetCost(
                    operation.SelectionSet,
                    rootType,
                    fragments,
                    activeFragments,
                    fragmentCostCache,
                    maskingCostCache,
                    schema,
                    ref spreadCounter,
                    maxSpreadExpansions));
            }
        }

        return Math.Max(1, totalCost);
    }

    private int CalculateSelectionSetCost(
        SelectionSetNode? selectionSet,
        IObjectTypeDefinition? currentType,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<string, int> fragmentCostCache,
        Dictionary<string, int> maskingCostCache,
        ISchemaDefinition schema,
        ref int spreadCounter,
        int maxSpreadExpansions)
    {
        if (selectionSet == null || selectionSet.Selections.Count == 0)
        {
            return 0;
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
                    foreach (var arg in field.Arguments)
                    {
                        if (IsRowLimitArgument(arg.Name.Value))
                        {
                            if (arg.Value is IntValueNode intVal && int.TryParse(intVal.Value, out var parsed))
                            {
                                requestedLimit = parsed;
                                break;
                            }

                            limitIsVariable = true;
                        }
                    }

                    int effectiveRows;
                    if (requestedLimit > 0)
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

                    int multiplier = isList ? _defaultListMultiplier : _payloadRowMultiplier;
                    cost = SafeAdd(cost, (int)Math.Min((long)int.MaxValue, (long)multiplier * effectiveRows));

                    if (field.SelectionSet != null)
                    {
                        cost = SafeAdd(cost, CalculateMaskingCost(field.SelectionSet, fragments, activeFragments, maskingCostCache));
                        cost = SafeAdd(cost, CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
                    }
                }
                else
                {
                    // Non-paginated entity, scalar, or unpaginated relation list
                    cost = SafeAdd(cost, isList ? 5 : 1);
                    if (field.SelectionSet != null)
                    {
                        cost = SafeAdd(cost, CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
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

                cost = SafeAdd(cost, CalculateSelectionSetCost(inlineFrag.SelectionSet, inlineType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
            }
            else if (selection is FragmentSpreadNode fragmentSpread)
            {
                spreadCounter++;
                if (spreadCounter > maxSpreadExpansions)
                {
                    return SafeAdd(_maxAllowedCost, 1000);
                }

                if (fragmentCostCache.TryGetValue(fragmentSpread.Name.Value, out var cachedFragCost))
                {
                    cost = SafeAdd(cost, cachedFragCost);
                }
                else if (fragments.TryGetValue(fragmentSpread.Name.Value, out var fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    IObjectTypeDefinition? fragType = currentType;
                    if (fragDef.TypeCondition != null && schema.Types.TryGetType<IObjectTypeDefinition>(fragDef.TypeCondition.Name.Value, out var foundType))
                    {
                        fragType = foundType;
                    }

                    int fragCost = CalculateSelectionSetCost(fragDef.SelectionSet, fragType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions);
                    activeFragments.Remove(fragDef.Name.Value);
                    fragmentCostCache[fragDef.Name.Value] = fragCost;
                    cost = SafeAdd(cost, fragCost);
                }
            }
        }

        return cost;
    }

    private static int CalculateMaskingCost(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<string, int> maskingCostCache)
    {
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
                maskingCost = SafeAdd(maskingCost, CalculateMaskingCost(inlineFrag.SelectionSet, fragments, activeFragments, maskingCostCache));
            }
            else if (childSel is FragmentSpreadNode spread)
            {
                if (maskingCostCache.TryGetValue(spread.Name.Value, out var cachedMaskCost))
                {
                    maskingCost = SafeAdd(maskingCost, cachedMaskCost);
                }
                else if (fragments.TryGetValue(spread.Name.Value, out var fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    int fragMaskCost = CalculateMaskingCost(fragDef.SelectionSet, fragments, activeFragments, maskingCostCache);
                    activeFragments.Remove(fragDef.Name.Value);
                    maskingCostCache[fragDef.Name.Value] = fragMaskCost;
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
}
