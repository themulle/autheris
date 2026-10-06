namespace TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Represents a single identifier (column, table, or alias name).
/// </summary>
public sealed record SqlIdentifier(string Value, bool IsQuoted = false) : SqlNode
{
    public override string ToString() => IsQuoted ? $"\"{Value.Replace("\"", "\"\"")}\"" : Value;
}
