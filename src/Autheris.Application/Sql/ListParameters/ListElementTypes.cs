namespace Autheris.Application.Sql.ListParameters;

using System;
using TrinoSqlEngine.Ast.Emit;

/// <summary>The element type of a declared list parameter (REQ-SQL-128, PLAN-SQL-128 A-4): CLR element type to bind type.</summary>
internal static class ListElementTypes
{
    /// <summary>A GUID is bound as its canonical "D" text (<paramref name="guidStrings"/>); there is no GUID bind type.</summary>
    public static SqlParameterType For(Type element, out bool guidStrings)
    {
        guidStrings = element == typeof(Guid);
        if (element == typeof(int)) return SqlParameterType.Int32;
        if (element == typeof(long)) return SqlParameterType.Int64;
        if (element == typeof(decimal)) return SqlParameterType.Decimal;
        if (element == typeof(double)) return SqlParameterType.Double;
        if (element == typeof(bool)) return SqlParameterType.Boolean;
        if (element == typeof(DateTimeOffset) || element == typeof(DateTime)) return SqlParameterType.TimestampTz;
        if (element == typeof(string) || element == typeof(Guid)) return SqlParameterType.String;
        throw new ClientListException(GovernedSqlErrorCodes.ListParamType);
    }
}
