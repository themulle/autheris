namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using HotChocolate.Language;

public sealed class CdnCacheTagVisitor
{
    private static readonly HashSet<string> IdArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "key", "code", "slug", "customerId", "invoiceId", "orderId", "userId"
    };

    public static CacheTagDescriptor ExtractTags(DocumentNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var typeTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entityTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in document.Definitions)
        {
            if (def is OperationDefinitionNode operation && operation.Operation == OperationType.Query)
            {
                TraverseSelectionSet(operation.SelectionSet, typeTags, entityTags);
            }
        }

        return new CacheTagDescriptor(typeTags.ToList(), entityTags.ToList());
    }

    private static void TraverseSelectionSet(
        SelectionSetNode? selectionSet,
        HashSet<string> typeTags,
        HashSet<string> entityTags)
    {
        if (selectionSet == null || selectionSet.Selections.Count == 0) return;

        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode field)
            {
                var fieldName = field.Name.Value;
                if (fieldName.StartsWith("__", StringComparison.Ordinal)) continue;

                var normalizedTypeName = NormalizeTypeName(fieldName);
                typeTags.Add($"type_{normalizedTypeName}");

                // Extract entity tag from key arguments like id: "123"
                foreach (var arg in field.Arguments)
                {
                    if (IdArgumentNames.Contains(arg.Name.Value))
                    {
                        var argVal = ExtractValue(arg.Value);
                        if (!string.IsNullOrWhiteSpace(argVal))
                        {
                            entityTags.Add($"entity_{normalizedTypeName}_{argVal}");
                        }
                    }
                }

                if (field.SelectionSet != null)
                {
                    TraverseSelectionSet(field.SelectionSet, typeTags, entityTags);
                }
            }
            else if (selection is InlineFragmentNode inlineFrag)
            {
                if (inlineFrag.TypeCondition != null)
                {
                    typeTags.Add($"type_{inlineFrag.TypeCondition.Name.Value.ToLowerInvariant()}");
                }
                TraverseSelectionSet(inlineFrag.SelectionSet, typeTags, entityTags);
            }
        }
    }

    private static string NormalizeTypeName(string fieldName)
    {
        var lower = fieldName.ToLowerInvariant();
        if (lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 3)
        {
            return lower[..^3] + "y";
        }
        if (lower.EndsWith("es", StringComparison.Ordinal) && lower.Length > 2)
        {
            return lower[..^2];
        }
        if (lower.EndsWith('s') && lower.Length > 1 && !lower.EndsWith("ss", StringComparison.Ordinal))
        {
            return lower[..^1];
        }
        return lower;
    }

    private static string? ExtractValue(IValueNode valueNode)
    {
        return valueNode switch
        {
            StringValueNode strVal => strVal.Value,
            IntValueNode intVal => intVal.Value,
            _ => null
        };
    }
}
