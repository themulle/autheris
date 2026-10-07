namespace Autheris.Application.Sql;

using System;
using System.Text.Json;

/// <summary>
/// WebSQL client parameters arrive as JSON. ADO.NET providers cannot bind <see cref="JsonElement"/>, so the values are
/// converted to plain CLR scalars before they are bound as command parameters.
/// </summary>
public static class WebSqlParameterValues
{
    /// <summary>
    /// Converts a JSON value to string, long, decimal, double, bool or null. Arrays and objects are not valid SQL parameter
    /// values and raise an <see cref="ArgumentException"/> (answered as 400).
    /// </summary>
    public static object? Normalize(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    return integer;
                }

                if (element.TryGetDecimal(out var number))
                {
                    return number;
                }

                return element.GetDouble();
            default:
                throw new ArgumentException("SQL parameters must be scalar values (string, number, boolean or null).");
        }
    }
}
