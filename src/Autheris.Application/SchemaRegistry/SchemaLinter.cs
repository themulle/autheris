namespace Autheris.Application.SchemaRegistry;

using HotChocolate.Language;

public sealed class SchemaLinter : ISchemaLinter
{
    public SchemaDiffResult Compare(string baselineSdl, string targetSdl)
    {
        var baselineDoc = Utf8GraphQLParser.Parse(baselineSdl);
        var targetDoc = Utf8GraphQLParser.Parse(targetSdl);
        return Compare(baselineDoc, targetDoc);
    }

    public SchemaDiffResult Compare(DocumentNode baselineDoc, DocumentNode targetDoc)
    {
        var changes = new List<SchemaChange>();

        var baselineTypes = baselineDoc.Definitions
            .OfType<ITypeDefinitionNode>()
            .ToDictionary(d => d.Name.Value, d => d, StringComparer.Ordinal);

        var targetTypes = targetDoc.Definitions
            .OfType<ITypeDefinitionNode>()
            .ToDictionary(d => d.Name.Value, d => d, StringComparer.Ordinal);

        // 1. Check for removed types
        foreach (var (typeName, baseType) in baselineTypes)
        {
            if (!targetTypes.TryGetValue(typeName, out var targetType))
            {
                changes.Add(SchemaChange.Breaking("TYPE_REMOVED", typeName, $"Type '{typeName}' was removed from the schema."));
                continue;
            }

            // Check if kind of type changed (e.g. object to scalar, etc.)
            if (baseType.Kind != targetType.Kind)
            {
                changes.Add(SchemaChange.Breaking("TYPE_KIND_CHANGED", typeName, $"Type '{typeName}' kind changed from {baseType.Kind} to {targetType.Kind}."));
                continue;
            }

            // Deep diff based on type kind
            switch (baseType)
            {
                case ObjectTypeDefinitionNode baseObj when targetType is ObjectTypeDefinitionNode targetObj:
                    DiffObjectOrInterfaceFields(typeName, baseObj.Fields, targetObj.Fields, changes);
                    break;

                case InterfaceTypeDefinitionNode baseIface when targetType is InterfaceTypeDefinitionNode targetIface:
                    DiffObjectOrInterfaceFields(typeName, baseIface.Fields, targetIface.Fields, changes);
                    break;

                case EnumTypeDefinitionNode baseEnum when targetType is EnumTypeDefinitionNode targetEnum:
                    DiffEnumValues(typeName, baseEnum.Values, targetEnum.Values, changes);
                    break;

                case InputObjectTypeDefinitionNode baseInput when targetType is InputObjectTypeDefinitionNode targetInput:
                    DiffInputFields(typeName, baseInput.Fields, targetInput.Fields, changes);
                    break;

                case UnionTypeDefinitionNode baseUnion when targetType is UnionTypeDefinitionNode targetUnion:
                    DiffUnionMembers(typeName, baseUnion.Types, targetUnion.Types, changes);
                    break;
            }
        }

        // 2. Check for added types
        foreach (var (typeName, _) in targetTypes)
        {
            if (!baselineTypes.ContainsKey(typeName))
            {
                changes.Add(SchemaChange.Safe("TYPE_ADDED", typeName, $"Type '{typeName}' was added to the schema."));
            }
        }

        return SchemaDiffResult.FromChanges(changes);
    }

    private static void DiffObjectOrInterfaceFields(
        string typeName,
        IReadOnlyList<FieldDefinitionNode> baseFields,
        IReadOnlyList<FieldDefinitionNode> targetFields,
        List<SchemaChange> changes)
    {
        var baseFieldMap = baseFields.ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);
        var targetFieldMap = targetFields.ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        foreach (var (fieldName, baseField) in baseFieldMap)
        {
            if (!targetFieldMap.TryGetValue(fieldName, out var targetField))
            {
                changes.Add(SchemaChange.Breaking("FIELD_REMOVED", $"{typeName}.{fieldName}", $"Field '{typeName}.{fieldName}' was removed."));
                continue;
            }

            var baseTypeStr = RenderTypeNode(baseField.Type);
            var targetTypeStr = RenderTypeNode(targetField.Type);

            if (!string.Equals(baseTypeStr, targetTypeStr, StringComparison.Ordinal))
            {
                changes.Add(SchemaChange.Breaking("FIELD_TYPE_CHANGED", $"{typeName}.{fieldName}",
                    $"Field '{typeName}.{fieldName}' type changed from '{baseTypeStr}' to '{targetTypeStr}'."));
            }

            // Check deprecation directive
            bool baseDeprecated = HasDirective(baseField.Directives, "deprecated");
            bool targetDeprecated = HasDirective(targetField.Directives, "deprecated");
            if (!baseDeprecated && targetDeprecated)
            {
                changes.Add(SchemaChange.Dangerous("FIELD_DEPRECATED", $"{typeName}.{fieldName}",
                    $"Field '{typeName}.{fieldName}' was marked as deprecated."));
            }

            // Compare field arguments
            DiffFieldArguments(typeName, fieldName, baseField.Arguments, targetField.Arguments, changes);
        }

        foreach (var (fieldName, _) in targetFieldMap)
        {
            if (!baseFieldMap.ContainsKey(fieldName))
            {
                changes.Add(SchemaChange.Safe("FIELD_ADDED", $"{typeName}.{fieldName}", $"Field '{typeName}.{fieldName}' was added."));
            }
        }
    }

    private static void DiffFieldArguments(
        string typeName,
        string fieldName,
        IReadOnlyList<InputValueDefinitionNode> baseArgs,
        IReadOnlyList<InputValueDefinitionNode> targetArgs,
        List<SchemaChange> changes)
    {
        var baseArgMap = baseArgs.ToDictionary(a => a.Name.Value, a => a, StringComparer.Ordinal);
        var targetArgMap = targetArgs.ToDictionary(a => a.Name.Value, a => a, StringComparer.Ordinal);

        foreach (var (argName, baseArg) in baseArgMap)
        {
            if (!targetArgMap.TryGetValue(argName, out var targetArg))
            {
                changes.Add(SchemaChange.Breaking("FIELD_ARGUMENT_REMOVED", $"{typeName}.{fieldName}({argName})",
                    $"Argument '{argName}' was removed from field '{typeName}.{fieldName}'."));
                continue;
            }

            var baseArgType = RenderTypeNode(baseArg.Type);
            var targetArgType = RenderTypeNode(targetArg.Type);

            if (!string.Equals(baseArgType, targetArgType, StringComparison.Ordinal))
            {
                changes.Add(SchemaChange.Breaking("FIELD_ARGUMENT_TYPE_CHANGED", $"{typeName}.{fieldName}({argName})",
                    $"Argument '{argName}' on field '{typeName}.{fieldName}' changed type from '{baseArgType}' to '{targetArgType}'."));
            }
        }

        foreach (var (argName, targetArg) in targetArgMap)
        {
            if (!baseArgMap.ContainsKey(argName))
            {
                bool isRequired = targetArg.Type is NonNullTypeNode && targetArg.DefaultValue == null;
                if (isRequired)
                {
                    changes.Add(SchemaChange.Breaking("REQUIRED_FIELD_ARGUMENT_ADDED", $"{typeName}.{fieldName}({argName})",
                        $"Required argument '{argName}: {RenderTypeNode(targetArg.Type)}' without default value was added to field '{typeName}.{fieldName}'."));
                }
                else
                {
                    changes.Add(SchemaChange.Safe("FIELD_ARGUMENT_ADDED", $"{typeName}.{fieldName}({argName})",
                        $"Optional argument '{argName}' was added to field '{typeName}.{fieldName}'."));
                }
            }
        }
    }

    private static void DiffEnumValues(
        string typeName,
        IReadOnlyList<EnumValueDefinitionNode> baseValues,
        IReadOnlyList<EnumValueDefinitionNode> targetValues,
        List<SchemaChange> changes)
    {
        var baseValMap = baseValues.ToDictionary(v => v.Name.Value, v => v, StringComparer.Ordinal);
        var targetValMap = targetValues.ToDictionary(v => v.Name.Value, v => v, StringComparer.Ordinal);

        foreach (var (valName, _) in baseValMap)
        {
            if (!targetValMap.ContainsKey(valName))
            {
                changes.Add(SchemaChange.Breaking("ENUM_VALUE_REMOVED", $"{typeName}.{valName}",
                    $"Enum value '{valName}' was removed from enum '{typeName}'."));
            }
        }

        foreach (var (valName, _) in targetValMap)
        {
            if (!baseValMap.ContainsKey(valName))
            {
                changes.Add(SchemaChange.Dangerous("ENUM_VALUE_ADDED", $"{typeName}.{valName}",
                    $"Enum value '{valName}' was added to enum '{typeName}'. (May cause unhandled enum variant warnings in generated clients)"));
            }
        }
    }

    private static void DiffInputFields(
        string typeName,
        IReadOnlyList<InputValueDefinitionNode> baseFields,
        IReadOnlyList<InputValueDefinitionNode> targetFields,
        List<SchemaChange> changes)
    {
        var baseFieldMap = baseFields.ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);
        var targetFieldMap = targetFields.ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        foreach (var (fieldName, baseField) in baseFieldMap)
        {
            if (!targetFieldMap.TryGetValue(fieldName, out var targetField))
            {
                changes.Add(SchemaChange.Breaking("INPUT_FIELD_REMOVED", $"{typeName}.{fieldName}",
                    $"Input field '{typeName}.{fieldName}' was removed."));
                continue;
            }

            var baseTypeStr = RenderTypeNode(baseField.Type);
            var targetTypeStr = RenderTypeNode(targetField.Type);

            if (!string.Equals(baseTypeStr, targetTypeStr, StringComparison.Ordinal))
            {
                changes.Add(SchemaChange.Breaking("INPUT_FIELD_TYPE_CHANGED", $"{typeName}.{fieldName}",
                    $"Input field '{typeName}.{fieldName}' type changed from '{baseTypeStr}' to '{targetTypeStr}'."));
            }
        }

        foreach (var (fieldName, targetField) in targetFieldMap)
        {
            if (!baseFieldMap.ContainsKey(fieldName))
            {
                bool isRequired = targetField.Type is NonNullTypeNode && targetField.DefaultValue == null;
                if (isRequired)
                {
                    changes.Add(SchemaChange.Breaking("REQUIRED_INPUT_FIELD_ADDED", $"{typeName}.{fieldName}",
                        $"Required input field '{typeName}.{fieldName}' of type '{RenderTypeNode(targetField.Type)}' without default value was added."));
                }
                else
                {
                    changes.Add(SchemaChange.Safe("OPTIONAL_INPUT_FIELD_ADDED", $"{typeName}.{fieldName}",
                        $"Optional input field '{typeName}.{fieldName}' was added."));
                }
            }
        }
    }

    private static void DiffUnionMembers(
        string typeName,
        IReadOnlyList<NamedTypeNode> baseMembers,
        IReadOnlyList<NamedTypeNode> targetMembers,
        List<SchemaChange> changes)
    {
        var baseSet = baseMembers.Select(m => m.Name.Value).ToHashSet(StringComparer.Ordinal);
        var targetSet = targetMembers.Select(m => m.Name.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var member in baseSet)
        {
            if (!targetSet.Contains(member))
            {
                changes.Add(SchemaChange.Breaking("UNION_MEMBER_REMOVED", $"{typeName}.{member}",
                    $"Union member '{member}' was removed from union '{typeName}'."));
            }
        }

        foreach (var member in targetSet)
        {
            if (!baseSet.Contains(member))
            {
                changes.Add(SchemaChange.Dangerous("UNION_MEMBER_ADDED", $"{typeName}.{member}",
                    $"Union member '{member}' was added to union '{typeName}'."));
            }
        }
    }

    private static string RenderTypeNode(ITypeNode node) => node switch
    {
        NonNullTypeNode nonNull => $"{RenderTypeNode(nonNull.Type)}!",
        ListTypeNode list => $"[{RenderTypeNode(list.Type)}]",
        NamedTypeNode named => named.Name.Value,
        _ => node.ToString() ?? "Unknown"
    };

    private static bool HasDirective(IReadOnlyList<DirectiveNode> directives, string name)
    {
        return directives.Any(d => string.Equals(d.Name.Value, name, StringComparison.OrdinalIgnoreCase));
    }
}
