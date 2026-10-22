namespace Autheris.Application.SchemaRegistry;

using HotChocolate.Language;

/// <summary>The merged form of an SDL document: type extensions folded into their definitions, uniqueness verified.</summary>
public sealed record NormalizedSchema(
    DocumentNode Document,
    IReadOnlyDictionary<string, string> RootOperationTypes,
    IReadOnlyDictionary<string, DirectiveDefinitionNode> Directives);

/// <summary>
/// MISC-16: normalises an SDL document before it is registered or compared. <c>extend type</c> nodes are merged into their definitions
/// (an extension without a definition is an error), and duplicate types, fields, arguments, enum values, input fields and directive
/// definitions are reported as errors, so every later map built from the document is duplicate-safe.
/// </summary>
public static class SchemaDocumentNormalizer
{
    /// <summary>The normalised schema, or the list of errors (never both).</summary>
    public static (NormalizedSchema? Schema, IReadOnlyList<string> Errors) Normalize(DocumentNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        var definitions = new Dictionary<string, ITypeDefinitionNode>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var definition in document.Definitions.OfType<ITypeDefinitionNode>())
        {
            var name = definition.Name.Value;
            if (!definitions.TryAdd(name, definition))
            {
                errors.Add($"Duplicate type definition '{name}'.");
                continue;
            }

            order.Add(name);
        }

        foreach (var extension in document.Definitions.OfType<ITypeExtensionNode>())
        {
            var name = extension.Name.Value;
            if (!definitions.TryGetValue(name, out var target))
            {
                errors.Add($"Type extension '{name}' has no type definition.");
                continue;
            }

            var merged = Merge(target, extension, errors);
            if (merged != null)
            {
                definitions[name] = merged;
            }
        }

        foreach (var name in order)
        {
            CheckUniqueness(definitions[name], errors);
        }

        var directives = new Dictionary<string, DirectiveDefinitionNode>(StringComparer.Ordinal);
        foreach (var directive in document.Definitions.OfType<DirectiveDefinitionNode>())
        {
            if (!directives.TryAdd(directive.Name.Value, directive))
            {
                errors.Add($"Duplicate directive definition '@{directive.Name.Value}'.");
            }
        }

        var roots = ResolveRootOperationTypes(document, definitions, errors);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var normalizedDocument = new DocumentNode(
            order.Select(n => (IDefinitionNode)definitions[n])
                .Concat(directives.Values)
                .Concat(document.Definitions.OfType<SchemaDefinitionNode>())
                .ToList());
        return (new NormalizedSchema(normalizedDocument, roots, directives), []);
    }

    private static ITypeDefinitionNode? Merge(ITypeDefinitionNode target, ITypeExtensionNode extension, List<string> errors)
    {
        switch (target, extension)
        {
            case (ObjectTypeDefinitionNode def, ObjectTypeExtensionNode ext):
                return def
                    .WithFields([.. def.Fields, .. ext.Fields])
                    .WithInterfaces([.. def.Interfaces, .. ext.Interfaces])
                    .WithDirectives([.. def.Directives, .. ext.Directives]);
            case (InterfaceTypeDefinitionNode def, InterfaceTypeExtensionNode ext):
                return def
                    .WithFields([.. def.Fields, .. ext.Fields])
                    .WithInterfaces([.. def.Interfaces, .. ext.Interfaces])
                    .WithDirectives([.. def.Directives, .. ext.Directives]);
            case (InputObjectTypeDefinitionNode def, InputObjectTypeExtensionNode ext):
                return def
                    .WithFields([.. def.Fields, .. ext.Fields])
                    .WithDirectives([.. def.Directives, .. ext.Directives]);
            case (EnumTypeDefinitionNode def, EnumTypeExtensionNode ext):
                return def
                    .WithValues([.. def.Values, .. ext.Values])
                    .WithDirectives([.. def.Directives, .. ext.Directives]);
            case (UnionTypeDefinitionNode def, UnionTypeExtensionNode ext):
                return def
                    .WithTypes([.. def.Types, .. ext.Types])
                    .WithDirectives([.. def.Directives, .. ext.Directives]);
            case (ScalarTypeDefinitionNode, ScalarTypeExtensionNode):
                return target;
            default:
                errors.Add($"Type extension '{extension.Name.Value}' does not match the kind of its type definition.");
                return null;
        }
    }

    private static void CheckUniqueness(ITypeDefinitionNode type, List<string> errors)
    {
        var typeName = type.Name.Value;
        switch (type)
        {
            case ObjectTypeDefinitionNode obj:
                CheckFields(typeName, obj.Fields, errors);
                break;
            case InterfaceTypeDefinitionNode iface:
                CheckFields(typeName, iface.Fields, errors);
                break;
            case InputObjectTypeDefinitionNode input:
                CheckDuplicates(input.Fields.Select(f => f.Name.Value), $"input field of '{typeName}'", errors);
                break;
            case EnumTypeDefinitionNode enumType:
                CheckDuplicates(enumType.Values.Select(v => v.Name.Value), $"enum value of '{typeName}'", errors);
                break;
            case UnionTypeDefinitionNode union:
                CheckDuplicates(union.Types.Select(t => t.Name.Value), $"union member of '{typeName}'", errors);
                break;
        }
    }

    private static void CheckFields(string typeName, IReadOnlyList<FieldDefinitionNode> fields, List<string> errors)
    {
        CheckDuplicates(fields.Select(f => f.Name.Value), $"field of '{typeName}'", errors);
        foreach (var field in fields)
        {
            CheckDuplicates(field.Arguments.Select(a => a.Name.Value), $"argument of '{typeName}.{field.Name.Value}'", errors);
        }
    }

    private static void CheckDuplicates(IEnumerable<string> names, string what, List<string> errors)
    {
        foreach (var duplicate in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Duplicate {what} '{duplicate.Key}'.");
        }
    }

    private static Dictionary<string, string> ResolveRootOperationTypes(
        DocumentNode document, Dictionary<string, ITypeDefinitionNode> definitions, List<string> errors)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemaDefinitions = document.Definitions.OfType<SchemaDefinitionNode>().ToList();
        if (schemaDefinitions.Count > 1)
        {
            errors.Add("Duplicate schema definition.");
        }

        foreach (var schema in schemaDefinitions.Take(1))
        {
            foreach (var operation in schema.OperationTypes)
            {
                if (!roots.TryAdd(operation.Operation.ToString(), operation.Type.Name.Value))
                {
                    errors.Add($"Duplicate root operation type '{operation.Operation}'.");
                }
            }
        }

        if (schemaDefinitions.Count == 0)
        {
            foreach (var (operation, defaultName) in new[] { ("Query", "Query"), ("Mutation", "Mutation"), ("Subscription", "Subscription") })
            {
                if (definitions.ContainsKey(defaultName))
                {
                    roots[operation] = defaultName;
                }
            }
        }

        return roots;
    }
}
