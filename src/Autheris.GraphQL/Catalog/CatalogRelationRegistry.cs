namespace Autheris.GraphQL.Catalog;

using System.Threading;
using Autheris.Domain.Model;

/// <summary>
/// Issue #177: Thread-safe registry mapping GraphQL type and relation field names to their relation cardinality.
/// Static facade delegating to <see cref="ICatalogRelationRegistry"/>.
/// </summary>
public static class CatalogRelationRegistry
{
    private static readonly ICatalogRelationRegistry BuiltIn = new DefaultCatalogRelationRegistry();
    private static ICatalogRelationRegistry _current = BuiltIn;

    /// <summary>
    /// The active <see cref="ICatalogRelationRegistry"/> provider.
    /// </summary>
    public static ICatalogRelationRegistry Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? BuiltIn);
    }

    public static void Register(string typeName, string fieldName, RelationCardinality cardinality) =>
        Current.Register(typeName, fieldName, cardinality);

    public static bool TryGetCardinality(string? typeName, string? fieldName, out RelationCardinality cardinality) =>
        Current.TryGetCardinality(typeName, fieldName, out cardinality);

    public static void Clear() => Current.Clear();
}
