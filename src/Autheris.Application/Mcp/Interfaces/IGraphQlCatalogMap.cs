namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The catalog part of the GraphQL schema as seen by MCP: GraphQL names of a table and the tables a GraphQL document reads.
/// </summary>
public interface IGraphQlCatalogMap
{
    /// <summary>GraphQL names of a table; null when the table is not part of the GraphQL schema.</summary>
    Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Every catalog table a query document reads (root fields and nested relations; fragments expanded). Empty for a
    /// pure introspection query. Null (fail-closed) when the document cannot be parsed, is not a single query operation
    /// or selects a root field that is not a catalog table.
    /// </summary>
    Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default);
}
