namespace TrinoSqlEngine.Ast.Nodes;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Multi-part qualified name (e.g. catalog.schema.table or schema.table.column).
/// </summary>
public sealed record SqlQualifiedName(IReadOnlyList<SqlIdentifier> Parts) : SqlNode
{
    public SqlQualifiedName(params string[] parts) 
        : this(parts.Select(p => new SqlIdentifier(p)).ToList()) { }

    public string SimpleName => Parts.Count > 0 ? Parts[^1].Value : string.Empty;
    public bool IsSimple => Parts.Count == 1;

    public string? CatalogName => Parts.Count >= 3 ? Parts[0].Value : null;
    public string? SchemaName => Parts.Count >= 2 ? Parts[^2].Value : null;
    public string TableOrColumnName => SimpleName;
    public string NormalizedName => string.Join(".", Parts.Select(p => p.Value));

    public override string ToString() => string.Join(".", Parts.Select(p => p.ToString()));
}
