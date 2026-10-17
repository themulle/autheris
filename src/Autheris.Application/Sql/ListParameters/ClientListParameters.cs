namespace Autheris.Application.Sql.ListParameters;

using System;
using System.Collections.Generic;
using System.Text.Json;
using TrinoSqlEngine.Ast.Emit;

/// <summary>Shape derivation and the federation guard for list-valued client parameters.</summary>
public static class ClientListParameters
{
    /// <summary>
    /// Shapes of the lists that the statement references (<paramref name="referencedNames"/>, case-insensitive); a supplied list the SQL
    /// does not reference is dropped, so it neither enters the template cache key nor widens a limit (SEC-128-04).
    /// </summary>
    public static ClientListShapes Shapes(IReadOnlyDictionary<string, object?> clientValues, IReadOnlySet<string> referencedNames)
    {
        ArgumentNullException.ThrowIfNull(clientValues);
        ArgumentNullException.ThrowIfNull(referencedNames);

        Dictionary<string, int>? counts = null;
        HashSet<string>? referenced = null;
        foreach (var (name, value) in clientValues)
        {
            if (value is not ClientListValue list)
            {
                continue;
            }

            referenced ??= new HashSet<string>(referencedNames, StringComparer.OrdinalIgnoreCase);
            if (!referenced.Contains(name))
            {
                continue;
            }

            counts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            counts.TryAdd(name, list.Count);   // spellings that differ only in case are one list: the first one wins
        }

        if (counts is null)
        {
            return ClientListShapes.Empty;
        }

        try
        {
            return ClientListShapes.Create(counts);
        }
        catch (ArgumentException)
        {
            // A list name the compiler would not accept as a client parameter name (too long, invalid characters): a stable code, no name.
            throw new ClientListException(GovernedSqlErrorCodes.ListParamPosition);
        }
    }

    /// <summary>
    /// Refuses a request whose RAW parameters contain a list (a JSON array, a collection or a <see cref="ClientListValue"/>) with
    /// SQL_LIST_PARAM_UNSUPPORTED. The federated executor never sees parameters, so a list would be dropped silently (SEC-128-12).
    /// </summary>
    public static void RejectForFederation(IReadOnlyDictionary<string, object?>? rawParameters)
    {
        if (rawParameters is null)
        {
            return;
        }

        foreach (object? value in rawParameters.Values)
        {
            if (value is ClientListValue || ListParameterNormalizer.IsListCandidate(value))
            {
                throw new ClientListException(GovernedSqlErrorCodes.ListParamUnsupported);
            }
        }
    }
}
