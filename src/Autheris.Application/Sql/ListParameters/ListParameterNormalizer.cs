namespace Autheris.Application.Sql.ListParameters;

using System;
using TrinoSqlEngine.Ast.Emit;

/// <summary>Turns a raw list value into a <see cref="ClientListValue"/>. Throws <see cref="ClientListException"/> only; messages carry no values.</summary>
public static class ListParameterNormalizer
{
    public static bool IsListCandidate(object? raw) => throw new NotImplementedException();

    public static ClientListValue Normalize(object raw, ListParameterLimits limits, SqlParameterType? declaredElementType) => throw new NotImplementedException();
}
