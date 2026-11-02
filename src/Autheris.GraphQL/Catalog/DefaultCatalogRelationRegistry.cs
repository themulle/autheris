namespace Autheris.GraphQL.Catalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Default thread-safe implementation of <see cref="ICatalogRelationRegistry"/>.
/// </summary>
public sealed class DefaultCatalogRelationRegistry : ICatalogRelationRegistry
{
    private readonly ConcurrentDictionary<(string TypeName, string FieldName), RelationCardinality> _registry =
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

    public void Register(string typeName, string fieldName, RelationCardinality cardinality)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(fieldName)) return;
        _registry[(typeName, fieldName)] = cardinality;
    }

    public bool TryGetCardinality(string? typeName, string? fieldName, out RelationCardinality cardinality)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(fieldName))
        {
            cardinality = RelationCardinality.OneToMany;
            return false;
        }

        if (_registry.TryGetValue((typeName, fieldName), out cardinality))
        {
            return true;
        }

        cardinality = RelationCardinality.OneToMany;
        return false;
    }

    public void Clear() => _registry.Clear();
}
