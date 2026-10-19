namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using HotChocolate.Language;

/// <summary>
/// SEC-GOV113-01: AST classifier for self-service mutations accessible by read-only tokens.
/// Inspects GraphQL operation definitions and field selections: returns true iff ALL selected root fields
/// are exclusively in the allowlist ('requestTableAccess', 'withdrawAccessRequest').
/// Aliases, introspection fields, fragments or unallowlisted mutations fail closed.
/// </summary>
public static class SelfServiceMutationClassifier
{
    private static readonly HashSet<string> PermittedSelfServiceMutations = new(StringComparer.Ordinal)
    {
        "requestTableAccess",
        "withdrawAccessRequest"
    };

    public static bool IsExclusivelySelfServiceMutation(DocumentNode document, string? operationName)
    {
        ArgumentNullException.ThrowIfNull(document);

        var operations = document.Definitions.OfType<OperationDefinitionNode>()
            .Where(o => string.IsNullOrEmpty(operationName) || string.Equals(o.Name?.Value, operationName, StringComparison.Ordinal))
            .ToList();

        if (operations.Count == 0) return false;

        bool hasMutation = false;
        foreach (var op in operations)
        {
            if (op.Operation != OperationType.Mutation) continue;
            hasMutation = true;

            if (op.SelectionSet.Selections.Count == 0) return false;

            foreach (var selection in op.SelectionSet.Selections)
            {
                if (selection is not FieldNode field) return false; // Fail closed on fragment spreads / inline fragments
                if (!PermittedSelfServiceMutations.Contains(field.Name.Value)) return false;
            }
        }

        return hasMutation;
    }
}
