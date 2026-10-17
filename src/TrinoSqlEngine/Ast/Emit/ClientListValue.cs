namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;

/// <summary>
/// A normalized client list value (REQ-SQL-128): immutable, non-null elements of one CLR type that matches
/// <see cref="ElementType"/>. The binder reads single elements by index; the value never reaches SQL text, logs or exception text.
/// </summary>
public sealed class ClientListValue
{
    private readonly object[] _elements;

    private ClientListValue(object[] elements, SqlParameterType elementType)
    {
        _elements = elements;
        ElementType = elementType;
    }

    public SqlParameterType ElementType { get; }

    public int Count => _elements.Length;

    public object this[int index] => _elements[index];

    /// <summary>Validates that every element is non-null and has the CLR type of <paramref name="elementType"/>; the elements are copied.</summary>
    public static ClientListValue Create(IReadOnlyList<object> elements, SqlParameterType elementType)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var expected = ClrTypeOf(elementType)
            ?? throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
        var copy = new object[elements.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            object? element = elements[i];
            if (element is null)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamNull);
            }

            if (element.GetType() != expected)
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
            }

            copy[i] = element;
        }

        return new ClientListValue(copy, elementType);
    }

    /// <summary>The CLR type every element of a list of this type must have; <c>null</c> for a type that cannot be a list element.</summary>
    internal static Type? ClrTypeOf(SqlParameterType type) => type switch
    {
        SqlParameterType.String => typeof(string),
        SqlParameterType.Int32 => typeof(int),
        SqlParameterType.Int64 => typeof(long),
        SqlParameterType.Decimal => typeof(decimal),
        SqlParameterType.Double => typeof(double),
        SqlParameterType.Boolean => typeof(bool),
        SqlParameterType.Date => typeof(DateOnly),
        SqlParameterType.Timestamp => typeof(DateTime),
        SqlParameterType.TimestampTz => typeof(DateTimeOffset),
        _ => null
    };

    // INV-16: type and count only.
    public override string ToString() => $"ClientListValue {{ ElementType = {ElementType}, Count = {Count} }}";
}
