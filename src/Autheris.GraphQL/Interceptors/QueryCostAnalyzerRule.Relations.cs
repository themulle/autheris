namespace Autheris.GraphQL.Interceptors;

using System;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate.Language;
using HotChocolate.Types;

public sealed partial class QueryCostAnalyzerRule
{
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
        // Multiplier 1.0 is ONLY permitted when the schema field and catalog metadata
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
        var reg = _relationRegistry ?? CatalogRelationRegistry.Current;
        if (!string.IsNullOrEmpty(typeName) && reg.TryGetCardinality(typeName, fieldName, out var registryCard))
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
