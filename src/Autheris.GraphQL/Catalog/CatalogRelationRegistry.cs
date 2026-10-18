namespace Autheris.GraphQL.Catalog;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Model;

/// <summary>
/// Issue #177: Thread-safe registry mapping GraphQL type and relation field names to their relation cardinality.
/// Used by QueryCostAnalyzerRule to enable multiplicity-aware cost estimation for 1:1 and N:1 relations.
/// </summary>
public static class CatalogRelationRegistry
{
    private static readonly ConcurrentDictionary<(string TypeName, string FieldName), RelationCardinality> _registry =
        new(new TypeFieldComparer());

    private sealed class TypeFieldComparer : IEqualityComparer<(string TypeName, string FieldName)>
    {
        public bool Equals((string TypeName, string FieldName) x, (string TypeName, string FieldName) y) =>
            string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FieldName, y.FieldName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string TypeName, string FieldName) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FieldName));
    }

    public static void Register(string typeName, string fieldName, RelationCardinality cardinality)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(fieldName)) return;
        _registry[(typeName, fieldName)] = cardinality;
    }

    public static bool TryGetCardinality(string? typeName, string? fieldName, out RelationCardinality cardinality)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(fieldName))
        {
            cardinality = RelationCardinality.OneToMany;
            return false;
        }

        return _registry.TryGetValue((typeName, fieldName), out cardinality);
    }

    public static void Clear()
    {
        _registry.Clear();
    }
}
