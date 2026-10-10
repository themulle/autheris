namespace TrinoSqlEngine.Ast.Nodes;

/// <summary>Identity of an injected row-level-security predicate: the secured table and an ordinal per table.</summary>
public readonly record struct SecurityPredicateId(string TableIdentity, int Ordinal);
