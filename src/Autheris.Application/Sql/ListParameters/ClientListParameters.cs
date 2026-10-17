namespace Autheris.Application.Sql.ListParameters;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;

/// <summary>Shape derivation and the federation guard for list-valued client parameters.</summary>
public static class ClientListParameters
{
    /// <summary>Shapes of the lists that the statement references (<paramref name="referencedNames"/>, case-insensitive); other lists are dropped.</summary>
    public static ClientListShapes Shapes(IReadOnlyDictionary<string, object?> clientValues, IReadOnlySet<string> referencedNames) => throw new NotImplementedException();

    /// <summary>Refuses a request whose RAW parameters contain a list (any JSON array, collection or <see cref="ClientListValue"/>) with SQL_LIST_PARAM_UNSUPPORTED.</summary>
    public static void RejectForFederation(IReadOnlyDictionary<string, object?>? rawParameters) => throw new NotImplementedException();
}
