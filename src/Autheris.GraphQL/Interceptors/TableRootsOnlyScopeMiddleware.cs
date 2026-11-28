namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Security;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Tokens that carry a scope listed in <c>Gateway:GraphQL:TableRootsOnlyScopes</c> may only use catalog table root fields
/// (list, aggregate and connection roots) and <c>__typename</c>. Known-disallowed roots and every mutation or subscription
/// operation are rejected before validation and cost accounting with <see cref="Code"/> and HTTP 403. Names that the schema
/// does not know are not matched here, so the answer never reveals whether a table exists or is readable.
/// </summary>
public sealed class TableRootsOnlyScopeMiddleware(RequestDelegate next)
{
    public const string Code = "ROOT_FIELD_NOT_ALLOWED";

    private const string TypenameField = "__typename";

    public async ValueTask InvokeAsync(RequestContext context)
    {
        var scopes = context.RequestServices.GetService<IOptions<GatewayOptions>>()?.Value.GraphQL.TableRootsOnlyScopes;
        // A missing options service restricts every caller (fail closed); an explicitly empty list switches the feature off.
        if (scopes is { Count: 0 })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var principal = GraphQlCallerPrincipal.ResolveOrNull(context);
        if (scopes is not null && !IsRestricted(principal, scopes))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var document = context.OperationDocumentInfo?.Document;
        var disallowed = document is null
            ? "operation"
            : FindDisallowedRoot(document, context.Request.OperationName, context.Schema);
        if (disallowed is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<TableRootsOnlyScopeMiddleware>()
            .LogDebug("GraphQL request rejected for a table-roots-only token: root {Root}", disallowed);
        context.Result = new OperationResult(ImmutableList.Create(
            ErrorBuilder.New()
                .SetMessage($"Root field '{disallowed}' is not allowed for this token; only table root fields are permitted.")
                .SetCode(Code)
                .Build()))
        {
            ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.HttpStatusCode, HttpStatusCode.Forbidden)
        };
    }

    /// <summary>True when any scope claim of the principal matches a configured entry (bare name, case-insensitive).</summary>
    internal static bool IsRestricted(ClaimsPrincipal? principal, IReadOnlyList<string> scopes)
    {
        if (principal is null || scopes.Count == 0)
        {
            return false;
        }

        foreach (var tokenScope in TokenAccessScope.GetTokenScopes(principal))
        {
            foreach (var configured in scopes)
            {
                if (TokenAccessScope.IsSameScope(configured, tokenScope))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the schema name of the first known-disallowed root in document order (or a fixed literal), or null when none is found.
    /// </summary>
    internal static string? FindDisallowedRoot(DocumentNode document, string? operationName, ISchemaDefinition schema)
    {
        var fragments = document.Definitions.OfType<FragmentDefinitionNode>()
            .GroupBy(f => f.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var operation in document.Definitions.OfType<OperationDefinitionNode>())
        {
            if (!string.IsNullOrEmpty(operationName) && !string.Equals(operation.Name?.Value, operationName, StringComparison.Ordinal))
            {
                continue;
            }

            switch (operation.Operation)
            {
                case OperationType.Mutation:
                    return "mutation";
                case OperationType.Subscription:
                    return "subscription";
            }

            var found = WalkRoots(operation.SelectionSet, fragments, schema);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? WalkRoots(SelectionSetNode root, Dictionary<string, FragmentDefinitionNode> fragments, ISchemaDefinition schema)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<IEnumerator<ISelectionNode>>();
        stack.Push(root.Selections.GetEnumerator());
        while (stack.Count > 0)
        {
            var top = stack.Peek();
            if (!top.MoveNext())
            {
                stack.Pop();
                continue;
            }

            switch (top.Current)
            {
                case FieldNode field:
                    var name = field.Name.Value;
                    if (name is "__schema" or "__type")
                    {
                        return name;
                    }

                    if (name != TypenameField
                        && schema.QueryType.Fields.TryGetField(name, out var definition)
                        && definition.Features.Get<CatalogRootFieldFeature>() is null)
                    {
                        return definition.Name;
                    }

                    break;
                case InlineFragmentNode inline:
                    stack.Push(inline.SelectionSet.Selections.GetEnumerator());
                    break;
                case FragmentSpreadNode spread:
                    if (visited.Add(spread.Name.Value) && fragments.TryGetValue(spread.Name.Value, out var fragment))
                    {
                        stack.Push(fragment.SelectionSet.Selections.GetEnumerator());
                    }

                    break;
            }
        }

        return null;
    }
}
