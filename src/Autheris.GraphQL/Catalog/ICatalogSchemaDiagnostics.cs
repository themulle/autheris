namespace Autheris.GraphQL.Catalog;

/// <summary>
/// BUG #157: read-only view of the catalog tables that the served GraphQL schema omits (no queryable columns, name
/// collision, unsupported dialect). The list is an immutable snapshot of the schema that is actually served.
/// </summary>
public interface ICatalogSchemaDiagnostics
{
    IReadOnlyList<CatalogOmittedTable> OmittedTables { get; }
}
