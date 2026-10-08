namespace TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Abstract base record of all SELECT projection items.
/// </summary>
public abstract record SelectItem : SqlNode;

/// <summary>
/// Column projection expression with optional alias.
/// </summary>
public sealed record ColumnSelectItem(
    Expression Expression,
    SqlIdentifier? Alias) : SelectItem;

/// <summary>
/// Wildcard projection item (e.g. * or table.*).
/// </summary>
public sealed record WildcardSelectItem(
    SqlQualifiedName? Qualifier) : SelectItem;
