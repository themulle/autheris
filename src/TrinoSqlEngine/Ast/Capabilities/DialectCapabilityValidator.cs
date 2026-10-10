namespace TrinoSqlEngine.Ast.Capabilities;

using System.Text;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Validates a secured AST against the capability table before emission: constructs the dialect cannot express, IN-list size
/// and identifier length. Over-limit input is rejected with a typed error, never split, truncated or compacted (AP-11, B-3).
/// </summary>
public static class DialectCapabilityValidator
{
    public static void Validate(SqlStatement statement, DialectCapabilities capabilities)
    {
        AstReflection.Walk(statement, node =>
        {
            switch (node)
            {
                case LateralTableSource when !capabilities.SupportsLateral:
                    throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "LATERAL");
                case InListExpression inList when capabilities.MaxInListItems is { } max && inList.Items.Count > max:
                    throw new SqlLimitExceededException(SqlLimitKind.InListItems, capabilities.Dialect, inList.Items.Count, max);
                case SqlIdentifier id:
                    int length = capabilities.IdentifierLengthUnit == IdentifierLengthUnit.Bytes
                        ? Encoding.UTF8.GetByteCount(id.Value)
                        : id.Value.Length;
                    if (length > capabilities.MaxIdentifierLength)
                    {
                        throw new SqlLimitExceededException(SqlLimitKind.IdentifierLength, capabilities.Dialect, length, capabilities.MaxIdentifierLength);
                    }

                    break;
            }

            return true;
        });
    }
}
