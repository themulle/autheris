namespace Autheris.GraphQL.Catalog;

using Autheris.Domain.Model;

/// <summary>
/// Issue #177: Thread-safe registry mapping GraphQL type and relation field names to their relation cardinality.
/// Used by QueryCostAnalyzerRule to enable multiplicity-aware cost estimation for 1:1 and N:1 relations.
/// </summary>
public interface ICatalogRelationRegistry
{
    /// <summary>
    /// Registers a relation field with its cardinality for the given GraphQL type.
    /// </summary>
    void Register(string typeName, string fieldName, RelationCardinality cardinality);

    /// <summary>
    /// Looks up the relation cardinality for the given type and field.
    /// Defaults to <see cref="RelationCardinality.OneToMany"/> if unregistered.
    /// </summary>
    bool TryGetCardinality(string? typeName, string? fieldName, out RelationCardinality cardinality);

    /// <summary>
    /// Clears all registered relations.
    /// </summary>
    void Clear();
}
