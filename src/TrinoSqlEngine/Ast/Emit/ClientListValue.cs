namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;

/// <summary>
/// A normalized client list value (REQ-SQL-128): immutable, non-null elements of one CLR type that matches
/// <see cref="ElementType"/>. The binder reads single elements by index; the value never reaches SQL text, logs or exception text.
/// </summary>
public sealed class ClientListValue
{
    private ClientListValue()
    {
    }

    public SqlParameterType ElementType => throw new NotImplementedException();

    public int Count => throw new NotImplementedException();

    public object this[int index] => throw new NotImplementedException();

    /// <summary>Validates that every element is non-null and has the CLR type of <paramref name="elementType"/>.</summary>
    public static ClientListValue Create(IReadOnlyList<object> elements, SqlParameterType elementType) => throw new NotImplementedException();

    // INV-16: type and count only.
    public override string ToString() => throw new NotImplementedException();
}
