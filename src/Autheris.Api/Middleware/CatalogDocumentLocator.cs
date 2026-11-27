using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;

namespace Autheris.Api.Middleware;

/// <summary>What kind of syntax node a validation error is expected to point at.</summary>
internal enum LocatedNodeKind
{
    UnknownField,
    KnownField,
    Argument,
    ObjectField,
    Value,
    ObjectValue,
    VariableUsage
}

/// <summary>
/// One catalog-related syntax node of a document. Names are taken from the syntax tree only, never from the engine.
/// <paramref name="Name"/> is the unknown object field name or the enum token for a value node.
/// </summary>
internal sealed record LocatedSubject(
    CatalogTableType Table,
    string? Field,
    string? Argument,
    string? Name,
    string? Variable,
    string? ExpectedType,
    bool IsUnknownColumn,
    HotChocolate.Language.Location Location);

/// <summary>
/// Pure walk of a GraphQL document against the catalog schema. It indexes every node that lies inside the selection set or the
/// arguments of a catalog table root or table field by source position, so a validation error can be mapped to the node it
/// concerns. Nodes elsewhere (directives, variable defaults, introspection, non-catalog roots) are not indexed and make
/// <see cref="TryLocate"/> fail. Returns <c>null</c> from <see cref="TryCreate"/> when the document has any other shape.
/// </summary>
internal sealed class CatalogDocumentLocator
{
    internal const int MaxDepth = 64;
    internal const int MaxNodes = 10_000;
    internal const int MaxTables = 32;

    private readonly Dictionary<(int Line, int Column), List<(LocatedNodeKind Kind, LocatedSubject Subject)>> _byPosition = [];
    private readonly Dictionary<TableIdentifierKey, CatalogTableType> _tables = [];
    private readonly Dictionary<string, CatalogTableType> _byFilterType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogTableType> _byOrderByType = new(StringComparer.Ordinal);
    private readonly ISchemaDefinition _schema;
    private CatalogSchemaModel _model = CatalogSchemaModel.Empty;
    private int _nodes;

    private readonly record struct TableIdentifierKey(string Domain, string Schema, string Name);

    private CatalogDocumentLocator(ISchemaDefinition schema)
    {
        _schema = schema;
    }

    /// <summary>Every catalog table the document touches plus the target of every relation of those tables.</summary>
    internal IReadOnlyCollection<CatalogTableType> Tables => _tables.Values;

    internal static CatalogDocumentLocator? TryCreate(DocumentNode document, ISchemaDefinition schema)
    {
        var locator = new CatalogDocumentLocator(schema);
        try
        {
            return locator.Build(document) ? locator : null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    internal bool TryLocate(int line, int column, LocatedNodeKind kind, out LocatedSubject subject)
    {
        subject = null!;
        if (!_byPosition.TryGetValue((line, column), out var entries) || entries.Count != 1 || entries[0].Kind != kind)
        {
            return false;
        }

        subject = entries[0].Subject;
        return true;
    }

    private bool Build(DocumentNode document)
    {
        if (_schema.QueryType is not { } queryType || !TryResolveModel(queryType))
        {
            return false;
        }

        foreach (var definition in document.Definitions)
        {
            switch (definition)
            {
                case OperationDefinitionNode operation:
                    if (operation.Operation != OperationType.Query)
                    {
                        return false;
                    }

                    if (!WalkSelectionSet(operation.SelectionSet, queryType, null, 1))
                    {
                        return false;
                    }

                    break;
                case FragmentDefinitionNode fragment:
                    if (!WalkFragmentDefinition(fragment, queryType))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        if (_tables.Count == 0)
        {
            return false;
        }

        return AddRelationTargets();
    }

    private bool AddRelationTargets()
    {
        foreach (var table in _tables.Values.ToList())
        {
            foreach (var relation in table.Relations)
            {
                if (!_model.TablesByIdentifier.TryGetValue(relation.TargetTableIdentifier, out var target))
                {
                    return false;
                }

                Register(target);
            }
        }

        return _tables.Count <= MaxTables;
    }

    private bool WalkFragmentDefinition(FragmentDefinitionNode fragment, IObjectTypeDefinition queryType)
    {
        var condition = fragment.TypeCondition.Name.Value;
        if (string.Equals(condition, queryType.Name, StringComparison.Ordinal))
        {
            return WalkSelectionSet(fragment.SelectionSet, queryType, null, 1);
        }

        if (_model.TablesByTypeName.TryGetValue(condition, out var table)
            && _schema.Types.TryGetType<IObjectTypeDefinition>(condition, out var type))
        {
            Register(table);
            return WalkSelectionSet(fragment.SelectionSet, type, table, 1);
        }

        return false;
    }

    private bool TryResolveModel(IObjectTypeDefinition queryType)
    {
        foreach (var field in queryType.Fields)
        {
            if (field.Features.Get<CatalogRootFieldFeature>() is { } feature)
            {
                SetModel(feature.SchemaModel);
                return true;
            }
        }

        return false;
    }

    private void SetModel(CatalogSchemaModel model)
    {
        if (_model != CatalogSchemaModel.Empty)
        {
            return;
        }

        _model = model;
        foreach (var table in model.Tables)
        {
            _byFilterType[table.FilterTypeName] = table;
            _byOrderByType[table.OrderByTypeName] = table;
        }
    }

    private void Register(CatalogTableType table)
    {
        var id = table.Identifier;
        _tables.TryAdd(new TableIdentifierKey(id.Domain, id.Schema, id.TableName), table);
    }

    private void Add(HotChocolate.Language.Location location, LocatedNodeKind kind, LocatedSubject subject)
    {
        var key = (location.Line, location.Column);
        if (!_byPosition.TryGetValue(key, out var list))
        {
            list = [];
            _byPosition[key] = list;
        }

        list.Add((kind, subject));
    }

    private bool Count(int depth) => depth <= MaxDepth && ++_nodes <= MaxNodes;

    private bool WalkSelectionSet(SelectionSetNode selectionSet, IObjectTypeDefinition parent, CatalogTableType? owner, int depth)
    {
        if (!Count(depth))
        {
            return false;
        }

        foreach (var selection in selectionSet.Selections)
        {
            if (!Count(depth))
            {
                return false;
            }

            switch (selection)
            {
                case FieldNode field:
                    if (!WalkField(field, parent, owner, depth))
                    {
                        return false;
                    }

                    break;
                case InlineFragmentNode inline:
                    if (inline.TypeCondition is { } condition
                        && !string.Equals(condition.Name.Value, parent.Name, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (!WalkSelectionSet(inline.SelectionSet, parent, owner, depth + 1))
                    {
                        return false;
                    }

                    break;
                case FragmentSpreadNode spread:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool WalkField(FieldNode field, IObjectTypeDefinition parent, CatalogTableType? owner, int depth)
    {
        var name = field.Name.Value;

        if (string.Equals(name, "__typename", StringComparison.Ordinal))
        {
            return field.Arguments.Count == 0 && field.SelectionSet is null;
        }

        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            return false;
        }

        if (!parent.Fields.TryGetField(name, out var definition))
        {
            if (owner is null || field.Location is not { } unknownLocation)
            {
                return false;
            }

            Add(unknownLocation, LocatedNodeKind.UnknownField, new LocatedSubject(owner, name, null, null, null, null, false, unknownLocation));
            return true;
        }

        CatalogTableType table;
        if (owner is null)
        {
            if (definition.Features.Get<CatalogRootFieldFeature>() is not { } feature)
            {
                return false;
            }

            SetModel(feature.SchemaModel);
            table = feature.Table;
            Register(table);
        }
        else
        {
            table = owner;
        }

        var named = Unwrap(definition.Type);
        var next = table;
        if (named is IObjectTypeDefinition objectType && _model.TablesByTypeName.TryGetValue(objectType.Name, out var entered))
        {
            Register(entered);
            next = entered;
        }

        if (field.Location is { } fieldLocation)
        {
            var missing = definition.Arguments
                .FirstOrDefault(a => a.Type.Kind == TypeKind.NonNull && a.DefaultValue is null && !field.Arguments.Any(x => string.Equals(x.Name.Value, a.Name, StringComparison.Ordinal)))?.Name;
            Add(fieldLocation, LocatedNodeKind.KnownField, new LocatedSubject(table, name, missing, null, null, null, false, fieldLocation));
        }

        foreach (var argument in field.Arguments)
        {
            if (!Count(depth))
            {
                return false;
            }

            if (argument.Location is not { } argumentLocation)
            {
                return false;
            }

            var argumentName = argument.Name.Value;
            Add(argumentLocation, LocatedNodeKind.Argument, new LocatedSubject(table, name, argumentName, null, null, null, false, argumentLocation));
            if (!definition.Arguments.TryGetField(argumentName, out var argumentDefinition))
            {
                continue;
            }

            if (!WalkValue(argument.Value, argumentDefinition.Type, new Context(table, name, argumentName), depth + 1))
            {
                return false;
            }
        }

        if (field.SelectionSet is { } selectionSet)
        {
            if (named is not IObjectTypeDefinition childType)
            {
                return false;
            }

            return WalkSelectionSet(selectionSet, childType, next, depth + 1);
        }

        return true;
    }

    private readonly record struct Context(CatalogTableType Table, string Field, string Argument);

    private bool WalkValue(IValueNode value, IType type, Context context, int depth)
    {
        if (!Count(depth))
        {
            return false;
        }

        var inner = type;
        while (inner is IWrapperType wrapper && inner.Kind == TypeKind.NonNull)
        {
            inner = wrapper.InnerType;
        }

        if (value is VariableNode variable)
        {
            if (variable.Location is { } variableLocation)
            {
                var variableName = variable.Name.Value;
                Add(variableLocation, LocatedNodeKind.VariableUsage, new LocatedSubject(
                    context.Table, context.Field, context.Argument, null, variableName, PrintType(type), false, variableLocation));
            }

            return true;
        }

        if (value.Location is { } valueLocation)
        {
            var token = value is EnumValueNode enumValue ? enumValue.Value : FirstUnknownGroupByToken(value, inner, context);
            var isGroupByToken = token is not null && string.Equals(context.Argument, "groupBy", StringComparison.Ordinal);
            Add(valueLocation, LocatedNodeKind.Value, new LocatedSubject(
                context.Table, context.Field, context.Argument, token, null, null, isGroupByToken, valueLocation));
        }

        if (inner.Kind == TypeKind.List && inner is IWrapperType listWrapper)
        {
            if (value is ListValueNode list)
            {
                foreach (var item in list.Items)
                {
                    if (!WalkValue(item, listWrapper.InnerType, context, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            return WalkValue(value, listWrapper.InnerType, context, depth + 1);
        }

        if (inner is IInputObjectTypeDefinition inputType && value is ObjectValueNode objectValue)
        {
            return WalkObjectValue(objectValue, inputType, context, depth);
        }

        return true;
    }

    /// <summary>
    /// The engine reports a group-by list with an unknown column at the list itself, not at the item. The first item that is not a
    /// value of the column enum is the unknown column.
    /// </summary>
    private static string? FirstUnknownGroupByToken(IValueNode value, IType listType, Context context)
    {
        if (value is not ListValueNode list
            || !string.Equals(context.Argument, "groupBy", StringComparison.Ordinal)
            || listType is not IWrapperType wrapper
            || Unwrap(wrapper.InnerType) is not IEnumTypeDefinition enumType)
        {
            return null;
        }

        foreach (var item in list.Items)
        {
            if (item is EnumValueNode enumItem && !enumType.Values.Any(v => string.Equals(v.Name, enumItem.Value, StringComparison.Ordinal)))
            {
                return enumItem.Value;
            }
        }

        return null;
    }

    private bool WalkObjectValue(ObjectValueNode objectValue, IInputObjectTypeDefinition inputType, Context context, int depth)
    {
        if (objectValue.Location is { } objectLocation)
        {
            Add(objectLocation, LocatedNodeKind.ObjectValue, new LocatedSubject(
                context.Table, context.Field, context.Argument, null, null, null, false, objectLocation));
        }

        var owningTable = _byFilterType.TryGetValue(inputType.Name, out var filterTable) ? filterTable
            : _byOrderByType.TryGetValue(inputType.Name, out var orderTable) ? orderTable
            : null;
        if (owningTable is not null)
        {
            Register(owningTable);
        }

        foreach (var objectField in objectValue.Fields)
        {
            if (!Count(depth))
            {
                return false;
            }

            if (objectField.Location is not { } fieldLocation)
            {
                return false;
            }

            var fieldName = objectField.Name.Value;
            if (!inputType.Fields.TryGetField(fieldName, out var fieldDefinition))
            {
                Add(fieldLocation, LocatedNodeKind.ObjectField, new LocatedSubject(
                    owningTable ?? context.Table, context.Field, context.Argument, fieldName, null, null, owningTable is not null, fieldLocation));
                continue;
            }

            Add(fieldLocation, LocatedNodeKind.ObjectField, new LocatedSubject(
                owningTable ?? context.Table, context.Field, context.Argument, fieldName, null, null, false, fieldLocation));
            if (!WalkValue(objectField.Value, fieldDefinition.Type, context, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private static IType Unwrap(IType type)
    {
        var current = type;
        while (current is IWrapperType wrapper)
        {
            current = wrapper.InnerType;
        }

        return current;
    }

    private static string PrintType(IType type) => type.Kind switch
    {
        TypeKind.NonNull => PrintType(((IWrapperType)type).InnerType) + "!",
        TypeKind.List => "[" + PrintType(((IWrapperType)type).InnerType) + "]",
        _ => ((INameProvider)type).Name
    };
}
